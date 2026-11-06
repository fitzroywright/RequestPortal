using System.Net;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Common.Secrets;
using Microsoft.Extensions.Configuration;
using Npgsql;

internal static class PostgresDeploymentPreflight
{
    internal static string Stage { get; private set; }="startup";
    private static InvalidOperationException Stop(string code,object details)
    {
        var error=new InvalidOperationException("Production preflight validation failed.");
        error.Data["PreflightCode"]=code;error.Data["PreflightDetails"]=details;return error;
    }
    internal sealed record Source(string Host,string EndpointAddress,string? Address,string Database,string Identity,int Major,bool Recovery);
    internal sealed record TableCount(string Schema,string Name,long Count);

    private static void Guard(NpgsqlConnectionStringBuilder settings)
    {
        if(!string.Equals(settings.Host,"axios.fftpj.local",StringComparison.OrdinalIgnoreCase) || settings.Database!="requestportal")
            throw Stop("PG_ENDPOINT_GUARD",new {hostMatches=string.Equals(settings.Host,"axios.fftpj.local",StringComparison.OrdinalIgnoreCase),databaseMatches=settings.Database=="requestportal"});
    }

    private static async Task<string> VerifyEndpointAsync(NpgsqlConnectionStringBuilder settings,CancellationToken ct)
    {
        Guard(settings);
        var addresses=await Dns.GetHostAddressesAsync(settings.Host!,ct);
        if(!addresses.Any(address=>address.Equals(IPAddress.Parse("10.10.0.226"))))
            throw Stop("PG_ENDPOINT_DNS",new {resolved=addresses.Select(address=>address.ToString()).ToArray()});
        return "10.10.0.226";
    }

    internal static async Task<object> RunAsync(IConfiguration configuration,ISecretProvider secrets,string? archive,CancellationToken ct)
    {
        if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException("The production preflight runs on Kratos.");
        // Managed secret only: never serialize the connection string or service environment.
        Stage="resolve-managed-secret";
        string value=await secrets.GetRequiredAsync("ConnectionStrings:RequestPortal",ct);
        Stage="configured-target";
        var settings=new NpgsqlConnectionStringBuilder(value);
        string endpoint=await VerifyEndpointAsync(settings,ct);
        Stage="open-postgresql";
        await using var connection=new NpgsqlConnection(value);await connection.OpenAsync(ct);
        await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await using(var command=new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='60s'; SET LOCAL idle_in_transaction_session_timeout='10min'",connection,transaction))
            await command.ExecuteNonQueryAsync(ct);
        Stage="actual-target-metadata";
        Source source;
        await using(var command=new NpgsqlCommand("SELECT inet_server_addr()::text,current_database(),current_user,current_setting('server_version_num')::int / 10000,pg_is_in_recovery()",connection,transaction))
        await using(var reader=await command.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);source=new(settings.Host!,endpoint,reader.IsDBNull(0)?null:reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3),reader.GetBoolean(4));
        }
        // inet_server_addr describes the backend side of this connection, not necessarily the client-facing endpoint.
        if(source.Database!="requestportal" || source.Recovery || source.Major is <13 or >18)
            throw Stop("PG_ACTUAL_TARGET_GUARD",new {source.Address,source.Database,source.Major,source.Recovery});
        Stage="migration-history";
        var migrations=new List<string>();
        await using(var command=new NpgsqlCommand("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"",connection,transaction))
        await using(var reader=await command.ExecuteReaderAsync(ct))
            while(await reader.ReadAsync(ct))migrations.Add(reader.GetString(0));
        if(!migrations.Contains("20261105070000_IntegrationExceptionDetails"))throw Stop("PG_BASELINE_MISSING",new {required="20261105070000_IntegrationExceptionDetails",applied=migrations});
        Stage="migration-privileges";
        var privileges=new Dictionary<string,bool>();
        await using(var command=new NpgsqlCommand("SELECT has_schema_privilege(current_schema(),'CREATE'),has_table_privilege('outbox_messages','INSERT')",connection,transaction))
        await using(var reader=await command.ExecuteReaderAsync(ct))
        {await reader.ReadAsync(ct);privileges["schemaCreate"]=reader.GetBoolean(0);privileges["outboxInsert"]=reader.GetBoolean(1);}
        await using(var command=new NpgsqlCommand("""
            SELECT required.name,COALESCE(has_table_privilege(to_regclass(required.name),'TRIGGER'),false)
            FROM (VALUES ('assistance_requests'),('request_status_history'),('quick_intakes'),('documents'),('deliveries'),
                         ('delivery_returns'),('organizations'),('request_categories'),('program_classifications'),('integration_links')) AS required(name)
            """,connection,transaction))
        await using(var reader=await command.ExecuteReaderAsync(ct))
            while(await reader.ReadAsync(ct))privileges["trigger:"+reader.GetString(0)]=reader.GetBoolean(1);
        if(privileges.Count!=12 || privileges.Values.Any(allowed=>!allowed))throw Stop("PG_PRIVILEGES_MISSING",new {source,privileges});
        if(archive is null)
        {
            await transaction.RollbackAsync(ct);
            return new {readOnly=true,status="preflight-passed",source,migrations,privileges};
        }
        if(!Path.IsPathFullyQualified(archive) || Path.GetFileName(archive)!="requestportal.dump" || File.Exists(archive))
            throw new InvalidOperationException("Unsafe or existing backup output.");
        string snapshot;
        await using(var command=new NpgsqlCommand("SELECT pg_export_snapshot()",connection,transaction))snapshot=(string)(await command.ExecuteScalarAsync(ct))!;
        var names=new List<(string Schema,string Name)>();
        await using(var command=new NpgsqlCommand("SELECT schemaname,tablename FROM pg_tables WHERE schemaname<>'information_schema' AND schemaname !~ '^pg_' ORDER BY schemaname,tablename",connection,transaction))
        await using(var reader=await command.ExecuteReaderAsync(ct))
            while(await reader.ReadAsync(ct))names.Add((reader.GetString(0),reader.GetString(1)));
        var counts=new List<TableCount>();var quote=new NpgsqlCommandBuilder();
        foreach(var name in names)
        {
            await using var command=new NpgsqlCommand("SELECT COUNT(*) FROM "+quote.QuoteIdentifier(name.Schema)+"."+quote.QuoteIdentifier(name.Name),connection,transaction);
            counts.Add(new(name.Schema,name.Name,(long)(await command.ExecuteScalarAsync(ct))!));
        }
        // Debian 13 supplies client 17, which supports older servers including the verified production server 13.
        // Never use a client older than the source server. The archive is verified on the isolated PostgreSQL 18 host.
        int clientMajor=Math.Max(17,source.Major);
        var start=new ProcessStartInfo($"/usr/lib/postgresql/{clientMajor}/bin/pg_dump") {UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"--format=custom","--quote-all-identifiers","--no-password","--lock-wait-timeout=5s","--snapshot="+snapshot,"--file="+archive})start.ArgumentList.Add(argument);
        foreach(string key in start.Environment.Keys.Where(key=>key.StartsWith("PG",StringComparison.Ordinal)).ToArray())start.Environment.Remove(key);
        start.Environment["PGHOST"]=settings.Host;start.Environment["PGPORT"]=settings.Port.ToString(CultureInfo.InvariantCulture);
        start.Environment["PGDATABASE"]=settings.Database;start.Environment["PGUSER"]=settings.Username;
        if(!string.IsNullOrEmpty(settings.Password))start.Environment["PGPASSWORD"]=settings.Password;
        start.Environment["PGSSLMODE"]=settings.SslMode switch {SslMode.VerifyCA=>"verify-ca",SslMode.VerifyFull=>"verify-full",_=>settings.SslMode.ToString().ToLowerInvariant()};
        if(!string.IsNullOrWhiteSpace(settings.RootCertificate))start.Environment["PGSSLROOTCERT"]=settings.RootCertificate;
        else if(settings.SslMode is SslMode.VerifyCA or SslMode.VerifyFull)
        {
            const string systemCa="/etc/ssl/certs/ca-certificates.crt";
            if(!File.Exists(systemCa))throw new InvalidOperationException("The existing TLS trust bundle is unavailable.");
            start.Environment["PGSSLROOTCERT"]=systemCa;
        }
        start.Environment["PGOPTIONS"]="-c default_transaction_read_only=on";
        using var process=Process.Start(start) ?? throw new InvalidOperationException("Unable to start PostgreSQL backup.");
        Task<string> stdout=process.StandardOutput.ReadToEndAsync(ct),stderr=process.StandardError.ReadToEndAsync(ct);
        try {await process.WaitForExitAsync(ct);await Task.WhenAll(stdout,stderr);}
        catch {if(!process.HasExited)process.Kill(entireProcessTree:true);throw;}
        if(process.ExitCode!=0)throw new InvalidOperationException("PostgreSQL backup did not complete; no migration was requested.");
        File.SetUnixFileMode(archive,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        await using var backupStream=File.OpenRead(archive);
        string sha=Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(backupStream,ct));
        await transaction.RollbackAsync(ct);
        return new {readOnly=true,status="backup-created-awaiting-isolated-restore",source,clientMajor,migrations,privileges,tables=counts,archiveSha256=sha,archiveBytes=new FileInfo(archive).Length,observedUtc=DateTimeOffset.UtcNow};
    }
    internal static async Task<object> VerifyCatalogueAsync(ISecretProvider secrets,CancellationToken ct)
    {
        Stage="verify-catalogue";
        string value=await secrets.GetRequiredAsync("ConnectionStrings:RequestPortal",ct);
        var settings=new NpgsqlConnectionStringBuilder(value);await VerifyEndpointAsync(settings,ct);
        await using var db=new RequestPortal.Infrastructure.Persistence.RequestPortalDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<RequestPortal.Infrastructure.Persistence.RequestPortalDbContext>().UseNpgsql(value).Options);
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='30s'",ct);
        bool target=await db.Database.SqlQueryRaw<bool>("SELECT host(inet_server_addr())='10.10.0.226' AND current_database()='requestportal' AND NOT pg_is_in_recovery() AND current_setting('server_version_num')::int/10000 BETWEEN 13 AND 18 AS \"Value\"").SingleAsync(ct);
        if(!target)throw Stop("PG_ACTUAL_TARGET_GUARD",new {databaseMatches=false});
        var evidence=await RequestPortal.Infrastructure.Management.ManagementDashboardSourceVerification.ReadAsync(db,null,ct);
        if(!evidence.MigrationPrivileges || evidence.DashboardTriggers!=evidence.ExpectedDashboardTriggers || !evidence.TriggerSetValid || !evidence.TriggerFunctionPresent)
            throw Stop("PG_SCHEMA_VERIFICATION",new {evidence.MigrationPrivileges,evidence.DashboardTriggers,evidence.TriggerFunctionPresent});
        var applied=await db.Database.GetAppliedMigrationsAsync(ct);
        if(!applied.Contains("20261106170310_ManagementDashboardAggregate"))throw Stop("PG_BASELINE_MISSING",new {required="20261106170310_ManagementDashboardAggregate"});
        await tx.RollbackAsync(ct);
        return new {readOnly=true,status="catalogue-verified",evidence,migration="20261106170310_ManagementDashboardAggregate"};
    }

    internal static async Task<object> VerifyDashboardAsync(ISecretProvider secrets,CancellationToken ct)
    {
        Stage="verify-aggregate";
        string value=await secrets.GetRequiredAsync("ConnectionStrings:RequestPortal",ct);
        var settings=new NpgsqlConnectionStringBuilder(value);await VerifyEndpointAsync(settings,ct);
        var options=new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<RequestPortal.Infrastructure.Persistence.RequestPortalDbContext>()
            .UseNpgsql(value).Options;
        await using var db=new RequestPortal.Infrastructure.Persistence.RequestPortalDbContext(options);
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout='30s'",ct);
        bool target=await db.Database.SqlQueryRaw<bool>("SELECT host(inet_server_addr())='10.10.0.226' AND current_database()='requestportal' AND NOT pg_is_in_recovery() AS \"Value\"").SingleAsync(ct);
        if(!target)throw new InvalidOperationException("Actual production PostgreSQL target verification failed.");
        var calculator=new RequestPortal.Infrastructure.Management.ManagementDashboardService(db,
            Microsoft.Extensions.Options.Options.Create(new RequestPortal.Infrastructure.Management.ManagementDashboardOptions()));
        var reader=new RequestPortal.Infrastructure.Management.PersistentManagementDashboardService(db,calculator);
        var watch=Stopwatch.StartNew();var first=await reader.GetDashboardAsync(ct);double cold=watch.Elapsed.TotalMilliseconds;
        watch.Restart();var second=await reader.GetDashboardAsync(ct);double warm=watch.Elapsed.TotalMilliseconds;
        if(first.SnapshotVersion<1 || first.SnapshotVersion!=second.SnapshotVersion || first.Parishes.Sum(x=>x.Requests)!=first.Statuses.Sum(x=>x.Count) ||
           first.Executive.Outstanding!=first.Parishes.Sum(x=>x.Outstanding) || first.Executive.Completed!=first.Parishes.Sum(x=>x.Completed))
            throw Stop("PG_AGGREGATE_TOTALS",new {first.SnapshotVersion});
        if(first.RefreshStatus!="Current" || first.LastUpdatedUtc is null || first.LastUpdatedUtc<DateTimeOffset.UtcNow.AddMinutes(-2))
            throw Stop("PG_AGGREGATE_STALE",new {first.SnapshotVersion,first.RefreshStatus,first.LastUpdatedUtc});
        var sourceEvidence=await RequestPortal.Infrastructure.Management.ManagementDashboardSourceVerification.ReadAsync(db,first,ct);
        if(!sourceEvidence.Reconciled)throw Stop("PG_SOURCE_TOTALS",new {first.SnapshotVersion});
        if(!sourceEvidence.MigrationPrivileges || sourceEvidence.DashboardTriggers!=sourceEvidence.ExpectedDashboardTriggers || !sourceEvidence.TriggerSetValid || !sourceEvidence.TriggerFunctionPresent)
            throw Stop("PG_SCHEMA_VERIFICATION",new {sourceEvidence.MigrationPrivileges,sourceEvidence.DashboardTriggers,sourceEvidence.TriggerFunctionPresent});
        var applied=await db.Database.GetAppliedMigrationsAsync(ct);
        if(!applied.Contains("20261106170310_ManagementDashboardAggregate"))throw Stop("PG_BASELINE_MISSING",new {required="20261106170310_ManagementDashboardAggregate"});
        int pending=await db.OutboxMessages.CountAsync(x=>x.MessageType==RequestPortal.Infrastructure.Management.ManagementDashboardAggregateRefresher.MessageType && x.ProcessedUtc==null,ct);
        await tx.RollbackAsync(ct);
        return new {readOnly=true,status="dashboard-verified",database=settings.Database,host=settings.Host,
            first.SnapshotVersion,first.GeneratedUtc,first.LastUpdatedUtc,first.LastReconciledUtc,first.RefreshStatus,
            totals=new {requests=first.Statuses.Sum(x=>x.Count),outstanding=first.Executive.Outstanding,completed=first.Executive.Completed},
            sourceEvidence,migration="20261106170310_ManagementDashboardAggregate",pendingRefreshes=pending,coldServiceMs=cold,warmServiceMs=warm,storageReads=0,
            measuredBrowserLoading=false,observedUtc=DateTimeOffset.UtcNow};
    }
}
