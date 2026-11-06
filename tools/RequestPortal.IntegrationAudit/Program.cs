using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Common.Secrets;
using FfpManager.Bridge;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

// Fixed SELECTs only. No website startup, EF migrations, queue dispatch, SQL repair or recovery writes.
if (args.Length<1 || args[0] is not ("portal" or "bridge" or "input-validation" or "database-preflight" or "database-backup" or "dashboard-verification" or "deployment-catalogue-verification")) return 2;
if(args.Length!=(args[0]=="database-backup"?2:1))return 2;
using var timeout = new CancellationTokenSource(args[0]=="database-backup"?TimeSpan.FromMinutes(10):TimeSpan.FromSeconds(90));
var token = timeout.Token;
try
{
    var builder = Host.CreateApplicationBuilder();
    var overrides = new Dictionary<string, string?> { ["CommonSecrets:OpenBao:DeleteSecretIdFileAfterRead"] = "false" };
    foreach (var provider in builder.Configuration.GetSection("CommonSecrets:Providers").GetChildren())
        overrides[$"CommonSecrets:Providers:{provider.Key}:Settings:DeleteSecretIdFileAfterRead"] = "false";
    builder.Configuration.AddInMemoryCollection(overrides);
    await using var secrets = CommonSecretsBootstrapRuntime.Create(builder.Configuration);
    if(args[0]=="deployment-catalogue-verification")
    {
        var report=await PostgresDeploymentPreflight.VerifyCatalogueAsync(secrets.Provider,token);
        Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions(JsonSerializerDefaults.Web)));return 0;
    }
    if(args[0]=="dashboard-verification")
    {
        var report=await PostgresDeploymentPreflight.VerifyDashboardAsync(secrets.Provider,token);
        Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions(JsonSerializerDefaults.Web)));return 0;
    }
    if(args[0] is "database-preflight" or "database-backup")
    {
        var report=await PostgresDeploymentPreflight.RunAsync(builder.Configuration,secrets.Provider,args[0]=="database-backup"?args[1]:null,token);
        Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions(JsonSerializerDefaults.Web)));return 0;
    }
    if(args[0]=="input-validation")
    {
        string readConnection=await secrets.Provider.GetRequiredAsync("ConnectionStrings:RequestPortal",token);
        var settings=new NpgsqlConnectionStringBuilder(readConnection);
        if(settings.Database!="requestportal" || settings.Host!="axios.fftpj.local")throw new InvalidOperationException("Production PostgreSQL target does not match.");
        await using var db=new RequestPortal.Infrastructure.Persistence.RequestPortalDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<RequestPortal.Infrastructure.Persistence.RequestPortalDbContext>()
                .UseNpgsql(readConnection).Options);
        var report=await new RequestPortal.Infrastructure.Management.InputDataQualityAudit(db).RunAsync(token);
        Console.WriteLine(JsonSerializer.Serialize(report,new JsonSerializerOptions(JsonSerializerDefaults.Web)));return 0;
    }
    var rows = new List<object>();
    if (args[0] == "portal")
    {
        string connectionString = await secrets.Provider.GetRequiredAsync("ConnectionStrings:RequestPortal", token);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using (var guard = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='30s'", connection, transaction))
            await guard.ExecuteNonQueryAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT q."RequestId", COALESCE(r."PublicReference",'(missing request)'), COALESCE(r."SubmittedUtc",q."CreatedUtc"), l."Id", l."Status", l."ExternalReference",
                   l."ApplicationNumber", l."LastAttemptUtc", l."SynchronizedUtc", l."LastError",
                   (SELECT count(*) FROM outbox_messages m WHERE m."Payload"->>'RequestId'=q."RequestId"::text AND m."MessageType" LIKE '%RequestSubmittedMessage'),
                   (SELECT count(*) FROM outbox_messages m WHERE m."Payload"->>'RequestId'=q."RequestId"::text AND m."MessageType" LIKE '%FfpManagerSubmissionRequestedMessage'),
                   (SELECT max(m."AttemptCount") FROM outbox_messages m WHERE m."Payload"->>'RequestId'=q."RequestId"::text AND m."MessageType" LIKE '%FfpManagerSubmissionRequestedMessage'),
                   (SELECT bool_or(m."ProcessedUtc" IS NOT NULL) FROM outbox_messages m WHERE m."Payload"->>'RequestId'=q."RequestId"::text AND m."MessageType" LIKE '%FfpManagerSubmissionRequestedMessage'),
                   r."Id" IS NOT NULL AS SourceRequestExists
            FROM quick_intakes q LEFT JOIN assistance_requests r ON r."Id"=q."RequestId"
            LEFT JOIN integration_links l ON l."RequestId"=q."RequestId" AND l."SystemName"=@System
            ORDER BY q."CreatedUtc",q."RequestId"
            """, connection, transaction);
        command.Parameters.AddWithValue("System", builder.Configuration["FfpManagerIntegration:SystemName"] ?? "FFPManager");
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                string? error = reader.IsDBNull(9) ? null : reader.GetString(9);
                rows.Add(new { requestId = reader.GetGuid(0), reference = reader.GetString(1), submittedUtc = Value(reader, 2),
                    linkId = Value(reader, 3), status = Value(reader, 4), applicationId = Value(reader, 5), applicationNumber = Value(reader, 6),
                    lastAttemptUtc = Value(reader, 7), synchronizedUtc = Value(reader, 8), errorClassification = Classify(error),
                    errorFingerprint = error is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(error))),
                    submissionEvents = reader.GetInt64(10), integrationEvents = reader.GetInt64(11), attempts = Value(reader, 12), queueProcessed = Value(reader, 13),
                    sourceRequestExists = reader.GetBoolean(14) });
            }
        var appliedMigrations = new List<string>();
        await using(var history = new NpgsqlCommand("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"", connection, transaction))
        await using(var reader = await history.ExecuteReaderAsync(token))
            while(await reader.ReadAsync(token)) appliedMigrations.Add(reader.GetString(0));
        await transaction.RollbackAsync(token);
        Console.WriteLine(JsonSerializer.Serialize(new { observedUtc = DateTimeOffset.UtcNow, readOnly = true, source = "RequestPortal PostgreSQL",
            server = connection.Host, port = connection.Port, database = connection.Database, appliedMigrations,
            integration = new { enabled = builder.Configuration.GetValue<bool>("FfpManagerIntegration:Enabled"),
                system = builder.Configuration["FfpManagerIntegration:SystemName"] ?? "FFPManager",
                bridgeHost = Uri.TryCreate(builder.Configuration["FfpManagerIntegration:BaseUrl"],UriKind.Absolute,out var origin) ? origin.Host : null,
                secretReference = builder.Configuration["FfpManagerIntegration:SharedSecretName"] ?? "FfpManagerIntegration:SharedSecret" }, records = rows }));
    }
    else
    {
        await BridgeStartupConfiguration.ResolveAndValidateAsync(builder.Configuration, secrets.Provider, true);
        var options = builder.Configuration.GetSection("Bridge").Get<BridgeOptions>() ?? new();
        if (options.SqlTarget != "Production") throw new InvalidOperationException();
        await using var connection = await FfpSqlConnection.OpenAsync(options.SqlConnectionString, "Production", token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await using (var command = new SqlCommand("""
            SELECT ID,ExternalRequestId,Code,ReferenceNo,CASE WHEN GCRecord IS NULL THEN 0 ELSE 1 END AS Archived
            FROM dbo.ApplicationLetters WHERE ExternalRequestId IS NOT NULL
                OR DocumentSource LIKE N'Request Portal%' OR AuditID LIKE N'RequestPortal:%' ORDER BY ID
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                rows.Add(new { applicationId = reader.GetInt64(0).ToString(), requestId = Value(reader, 1),
                    portalReference = Value(reader, 2), applicationNumber = Value(reader, 3), archived = reader.GetInt32(4) != 0 });
        var outbox = new List<object>();
        await using (var command = new SqlCommand("""
            SELECT PortalRequestId,COUNT(*) AS Pending,MAX(AttemptCount) AS Attempts,MIN(OccurredUtc) AS Oldest,
                   SUM(CASE WHEN AttemptCount>=@Maximum THEN 1 ELSE 0 END) AS Failed
            FROM dbo.RequestPortalIntegrationOutbox WHERE DeliveredUtc IS NULL GROUP BY PortalRequestId
            """, connection, transaction))
        {
            command.Parameters.Add("@Maximum", SqlDbType.Int).Value = options.OutboxMaxAttempts;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) outbox.Add(new { requestId = reader.GetGuid(0), pending = reader.GetInt32(1),
                attempts = reader.GetInt32(2), oldest = Value(reader, 3), failed = reader.GetInt32(4) });
        }
        await transaction.RollbackAsync(token);
        Console.WriteLine(JsonSerializer.Serialize(new { observedUtc = DateTimeOffset.UtcNow, readOnly = true,
            server = "JMDB01", database = "FFPJM", records = rows, returnOutbox = outbox }));
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", errorType = exception.GetType().Name,
        stage=PostgresDeploymentPreflight.Stage,
        code=exception.Data["PreflightCode"] as string ?? (exception is PostgresException postgres ? "SQL_"+postgres.SqlState : "PREFLIGHT_UNAVAILABLE"),
        details=exception.Data["PreflightDetails"] }));
    return 1;
}

static object? Value(System.Data.Common.DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
static string? Classify(string? error)
{
    if (error is null) return null;
    if (error.Contains("401") || error.Contains("403") || error.Contains("Login failed", StringComparison.OrdinalIgnoreCase)) return "AuthenticationEvidence";
    if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase) || error.Contains("connection", StringComparison.OrdinalIgnoreCase)) return "ConnectionEvidence";
    if (error.Contains("constraint", StringComparison.OrdinalIgnoreCase) || error.Contains("truncated", StringComparison.OrdinalIgnoreCase)) return "SqlConstraintEvidence";
    if (error.Contains("Parish", StringComparison.OrdinalIgnoreCase)) return "ParishMappingEvidence";
    if (error.Contains("400") || error.Contains("validation", StringComparison.OrdinalIgnoreCase)) return "ValidationEvidence";
    return "RequiresSanitizedJournalReview";
}
