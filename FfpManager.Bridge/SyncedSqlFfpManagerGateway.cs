using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public sealed class SyncedSqlFfpManagerGateway : IFfpManagerGateway
{
    private readonly BridgeOptions options;
    private readonly IFfpManagerReferenceDataCatalog referenceData;
    private readonly SqlFfpManagerGateway legacy;

    public SyncedSqlFfpManagerGateway(
        IOptions<BridgeOptions> options,
        IFfpManagerReferenceDataCatalog referenceData)
    {
        this.options = options.Value;
        this.referenceData = referenceData;
        legacy = new SqlFfpManagerGateway(options);
    }

    public async Task<ExternalApplicationReference> CreateApplicationAsync(ExternalApplicationSubmission submission, CancellationToken cancellationToken = default)
    {
        ValidateSubmission(submission);

        BridgeApplicantName.Validate(submission);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        await using (SqlCommand existing = new("SELECT TOP (1) ID, Code, ReferenceNo, DateCreated FROM dbo.ApplicationLetters WITH (UPDLOCK, HOLDLOCK) WHERE ExternalRequestId=@ExternalRequestId AND GCRecord IS NULL ORDER BY ID", connection, transaction))
        {
            existing.Parameters.Add("@ExternalRequestId", SqlDbType.UniqueIdentifier).Value = submission.PortalRequestId;
            await using SqlDataReader reader = await existing.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                long id = reader.GetInt64(0);
                string? referenceNo = reader.IsDBNull(2) ? null : reader.GetString(2);
                DateTimeOffset accepted = reader.IsDBNull(3)
                    ? submission.SubmittedUtc
                    : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc));
                await reader.CloseAsync();
                string existingReference = await FfpManagerReferenceNumberGenerator.EnsureExistingAsync(
                    connection, transaction, id, referenceNo, accepted, submission, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ExternalApplicationReference(id.ToString(), existingReference, accepted, true);
            }
        }

        var generatedReference = await FfpManagerReferenceNumberGenerator.GenerateAsync(
            connection, transaction, submission.Parish, submission.SubmittedUtc, cancellationToken);
        long statusId = await referenceData.ResolveApplicationStatusIdAsync(options.InitialApplicationStatus, cancellationToken);
        const string insert = """
            INSERT INTO dbo.ApplicationLetters
            (Code,ReferenceNo,Name,ContactPerson,FirstName,LastName,MainContactNo,ApplicationDate,DateReceived,Comments,Programme,Requirements,ApplicationStatus,UserName,DateCreated,DateUpdated,AuditID,CreatedBy,ExternalRequestId,Parish,DocumentSource)
            OUTPUT INSERTED.ID
            VALUES
            (@Code,@ReferenceNo,@Name,@ContactPerson,@FirstName,@LastName,@MainContactNo,@ApplicationDate,@DateReceived,@Comments,@Programme,@Requirements,@ApplicationStatus,@UserName,@DateCreated,@DateUpdated,@AuditID,@CreatedBy,@ExternalRequestId,@Parish,@DocumentSource);
            """;
        await using SqlCommand command = new(insert, connection, transaction);
        AddNullable(command, "@Code", submission.PublicReference, SqlDbType.NVarChar, 500);
        AddNullable(command, "@ReferenceNo", generatedReference.ReferenceNo, SqlDbType.NVarChar, 500);
        AddNullable(command, "@Name", submission.OrganizationName, SqlDbType.VarChar, 400);
        AddNullable(command, "@ContactPerson", submission.OrganizationName, SqlDbType.NVarChar, 255);
        AddNullable(command, "@FirstName", submission.FirstName, SqlDbType.NVarChar, 200);
        AddNullable(command, "@LastName", submission.LastName, SqlDbType.NVarChar, 100);
        AddNullable(command, "@MainContactNo", submission.OrganizationPhone, SqlDbType.NVarChar, 255);
        command.Parameters.Add("@ApplicationDate", SqlDbType.DateTime).Value = submission.SubmittedUtc.UtcDateTime;
        command.Parameters.Add("@DateReceived", SqlDbType.DateTime).Value = submission.SubmittedUtc.UtcDateTime;
        AddNullable(command, "@Comments", CombineComments(submission), SqlDbType.NVarChar, 2500);
        AddNullable(command, "@Programme", submission.ProgramCategory, SqlDbType.NVarChar, 255);
        AddNullable(command, "@Requirements", CombineRequirements(submission), SqlDbType.NVarChar, 255);
        command.Parameters.Add("@ApplicationStatus", SqlDbType.BigInt).Value = statusId;
        AddNullable(command, "@UserName", options.UserName, SqlDbType.NVarChar, 50);
        command.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = submission.SubmittedUtc.UtcDateTime;
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = submission.SubmittedUtc.UtcDateTime;
        AddNullable(command, "@AuditID", $"RequestPortal:{submission.PortalRequestId:D}", SqlDbType.NVarChar, 100);
        AddNullable(command, "@CreatedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
        command.Parameters.Add("@ExternalRequestId", SqlDbType.UniqueIdentifier).Value = submission.PortalRequestId;
        command.Parameters.Add("@Parish", SqlDbType.BigInt).Value = generatedReference.ParishId;
        AddNullable(command, "@DocumentSource", submission.DocumentSource, SqlDbType.NVarChar, 255);
        long applicationId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);

        foreach (ExternalApplicationItem item in submission.Items)
        {
            long unitId = await referenceData.ResolveUnitOfMeasureIdAsync(item.UnitOfMeasure, cancellationToken);
            const string itemSql = "INSERT INTO dbo.ItemsRequested (Code,Name,Description,UserName,DateCreated,DateUpdated,Quantity,ApplicationLetterID,UnitOfMeasure,CreatedBy) VALUES (@Code,@Name,@Description,@UserName,@DateCreated,@DateUpdated,@Quantity,@ApplicationLetterID,@UnitOfMeasure,@CreatedBy);";
            await using SqlCommand itemCommand = new(itemSql, connection, transaction);
            AddNullable(itemCommand, "@Code", item.PortalItemId.ToString("N")[..25], SqlDbType.NVarChar, 25);
            AddNullable(itemCommand, "@Name", Truncate(item.Description, 255), SqlDbType.NVarChar, 255);
            AddNullable(itemCommand, "@Description", item.Description, SqlDbType.NVarChar, 500);
            AddNullable(itemCommand, "@UserName", options.UserName, SqlDbType.NVarChar, 50);
            itemCommand.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = submission.SubmittedUtc.UtcDateTime;
            itemCommand.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = submission.SubmittedUtc.UtcDateTime;
            itemCommand.Parameters.Add("@Quantity", SqlDbType.Int).Value = decimal.ToInt32(item.Quantity);
            itemCommand.Parameters.Add("@ApplicationLetterID", SqlDbType.BigInt).Value = applicationId;
            itemCommand.Parameters.Add("@UnitOfMeasure", SqlDbType.BigInt).Value = unitId;
            AddNullable(itemCommand, "@CreatedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
            await itemCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ExternalApplicationReference(applicationId.ToString(), generatedReference.ReferenceNo, submission.SubmittedUtc, false);
    }

    public async Task SaveArtifactAsync(string applicationId, ExternalArtifactDescriptor descriptor, Stream content, CancellationToken cancellationToken = default)
    {
        long id = ParseApplicationId(applicationId);
        if (string.IsNullOrWhiteSpace(options.SupportingDocumentRoot)) throw new InvalidOperationException("Bridge:SupportingDocumentRoot is required.");
        string safeName = SanitizeFileName(descriptor.FileName);
        string finalPath = Path.Combine(options.SupportingDocumentRoot, $"ApplicationLetter-{id}-{descriptor.PortalDocumentId:N}-{safeName}");
        string managerPath = BridgeDocumentPath.ForManager(options, finalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        string tempPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (FileStream file = new(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await content.CopyToAsync(file, cancellationToken);
            await file.FlushAsync(cancellationToken);
        }
        File.Move(tempPath, finalPath, true);

        string mapped = options.DocumentTypeMap.TryGetValue(descriptor.DocumentType, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : descriptor.DocumentType;
        long documentTypeId;
        try { documentTypeId = await referenceData.ResolveDocumentTypeIdAsync(mapped, cancellationToken); }
        catch when (!string.IsNullOrWhiteSpace(options.DefaultDocumentType)) { documentTypeId = await referenceData.ResolveDocumentTypeIdAsync(options.DefaultDocumentType, cancellationToken); }

        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            IF EXISTS (SELECT 1 FROM dbo.SupportingDocument WHERE Oid=@Oid AND ApplicationLetter=@ApplicationLetter)
              UPDATE dbo.SupportingDocument SET DocumentNo=@DocumentNo,DocumentType=@DocumentType,Description=@Description,DateSubmitted=@DateSubmitted,SubmittedBy=@SubmittedBy,UserName=@UserName,DateUpdated=@DateUpdated,FileName=@FileName,CreatedBy=@CreatedBy WHERE Oid=@Oid AND ApplicationLetter=@ApplicationLetter;
            ELSE
              INSERT INTO dbo.SupportingDocument (Oid,DateCreated,DocumentNo,DocumentType,Description,DateSubmitted,SubmittedBy,UserName,DateUpdated,ApplicationLetter,FileName,CreatedBy) VALUES (@Oid,@DateCreated,@DocumentNo,@DocumentType,@Description,@DateSubmitted,@SubmittedBy,@UserName,@DateUpdated,@ApplicationLetter,@FileName,@CreatedBy);
            """;
        await using SqlCommand command = new(sql, connection);
        command.Parameters.Add("@Oid", SqlDbType.UniqueIdentifier).Value = descriptor.PortalDocumentId;
        command.Parameters.Add("@ApplicationLetter", SqlDbType.BigInt).Value = id;
        AddNullable(command, "@DocumentNo", $"ApplicationLetter-{id}", SqlDbType.NVarChar, 100);
        command.Parameters.Add("@DocumentType", SqlDbType.BigInt).Value = documentTypeId;
        AddNullable(command, "@Description", mapped, SqlDbType.NVarChar, 100);
        command.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = descriptor.UploadedUtc.UtcDateTime;
        command.Parameters.Add("@DateSubmitted", SqlDbType.DateTime).Value = descriptor.UploadedUtc.UtcDateTime;
        AddNullable(command, "@SubmittedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
        AddNullable(command, "@UserName", options.UserName, SqlDbType.NVarChar, 50);
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = DateTime.UtcNow;
        AddNullable(command, "@FileName", managerPath, SqlDbType.NVarChar, 1024);
        AddNullable(command, "@CreatedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ExternalApplicationStatus?> GetStatusAsync(string applicationId, CancellationToken cancellationToken = default)
    {
        long id = ParseApplicationId(applicationId);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT al.DateCreated,al.DateUpdated,ast.ID,ast.Name,ast.Description FROM dbo.ApplicationLetters al LEFT JOIN dbo.ApplicationStatusTypes ast ON ast.ID=al.ApplicationStatus WHERE al.ID=@ID AND al.GCRecord IS NULL;";
        await using SqlCommand command = new(sql, connection);
        command.Parameters.Add("@ID", SqlDbType.BigInt).Value = id;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        string name = reader.IsDBNull(3) ? "RECEIVED" : reader.GetString(3);
        string canonical = MapStatus(name);
        DateTime changed = !reader.IsDBNull(1) ? reader.GetDateTime(1) : !reader.IsDBNull(0) ? reader.GetDateTime(0) : DateTime.UtcNow;
        string? detail = reader.IsDBNull(4) ? null : reader.GetString(4);
        return new ExternalApplicationStatus(id.ToString(), canonical, name, new DateTimeOffset(DateTime.SpecifyKind(changed, DateTimeKind.Utc)), detail, false);
    }

    public Task SaveInformationResponseAsync(ExternalInformationResponse response, CancellationToken cancellationToken = default) => legacy.SaveInformationResponseAsync(response, cancellationToken);
    public Task<ExternalApplicationSubmission?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken = default) => legacy.GetApplicationAsync(applicationId, cancellationToken);

    public async Task<ExternalApplicationStatus> SetStatusAsync(string applicationId, string statusCode, string statusText, string? detail, bool terminal, CancellationToken cancellationToken = default)
    {
        long id = ParseApplicationId(applicationId);
        string requestedName = string.IsNullOrWhiteSpace(statusText) ? statusCode : statusText;
        long statusId = await referenceData.ResolveApplicationStatusIdAsync(requestedName, cancellationToken);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqlCommand command = new("UPDATE dbo.ApplicationLetters SET ApplicationStatus=@Status,DateUpdated=@DateUpdated WHERE ID=@ID", connection);
        command.Parameters.Add("@Status", SqlDbType.BigInt).Value = statusId;
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = DateTime.UtcNow;
        command.Parameters.Add("@ID", SqlDbType.BigInt).Value = id;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new KeyNotFoundException($"FFP Manager application '{applicationId}' was not found.");
        return await GetStatusAsync(applicationId, cancellationToken) ?? throw new KeyNotFoundException($"FFP Manager application '{applicationId}' was not found after status update.");
    }

    private static void ValidateSubmission(ExternalApplicationSubmission submission)
    {
        foreach (ExternalApplicationItem item in submission.Items)
        {
            if (item.Quantity != decimal.Truncate(item.Quantity) ||
                item.Quantity < int.MinValue ||
                item.Quantity > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"FFP Manager ItemsRequested.Quantity is an integer; portal item {item.PortalItemId:D} has quantity {item.Quantity}.");
            }
        }
    }

    private string MapStatus(string rawName)
    {
        if (options.StatusMap.TryGetValue(rawName, out string? mapped) && !string.IsNullOrWhiteSpace(mapped)) return NormalizeStatus(mapped);
        return NormalizeStatus(rawName);
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SqlConnectionString)) throw new InvalidOperationException("Bridge:SqlConnectionString is required when Bridge:Gateway is Sql.");
        return await FfpSqlConnection.OpenAsync(options.SqlConnectionString, options.SqlTarget, cancellationToken);
    }

    private static string NormalizeStatus(string value) => value.Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
    private static long ParseApplicationId(string value) => long.TryParse(value, out long id) && id > 0 ? id : throw new ArgumentException($"'{value}' is not a valid FFP Manager ApplicationLetters.ID.");
    private static string SanitizeFileName(string value) => BridgeDocumentPath.SafeFileName(value);
    private static string? Truncate(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
    private static string? CombineComments(ExternalApplicationSubmission s) => Truncate(string.Join(Environment.NewLine, new[] { s.Summary, s.Circumstances, s.OrganizationAddress, s.OrganizationEmail }.Where(x => !string.IsNullOrWhiteSpace(x))), 2500);
    private static string? CombineRequirements(ExternalApplicationSubmission s) => Truncate(string.Join(" / ", new[] { s.Category, s.Subcategory }.Where(x => !string.IsNullOrWhiteSpace(x))), 255);
    private static void AddNullable(SqlCommand command, string name, string? value, SqlDbType type, int size) { SqlParameter p = size == -1 ? command.Parameters.Add(name, type, -1) : command.Parameters.Add(name, type, size); p.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value; }
}
