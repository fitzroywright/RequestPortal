using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public sealed class SqlFfpManagerGateway : IFfpManagerGateway
{
    private readonly BridgeOptions options;

    public SqlFfpManagerGateway(IOptions<BridgeOptions> options)
    {
        this.options = options.Value;
    }

    public async Task<FfpManagerApplicationReference> CreateApplicationAsync(
        FfpManagerApplicationSubmission submission,
        CancellationToken cancellationToken = default)
    {
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        ExistingApplication? existing = await FindByPortalRequestIdAsync(connection, transaction, submission.PortalRequestId, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new FfpManagerApplicationReference(
                existing.Id.ToString(),
                existing.ReferenceNo ?? existing.Code ?? existing.Id.ToString(),
                existing.DateCreated ?? submission.SubmittedUtc,
                AlreadyExisted: true);
        }

        long? statusId = string.IsNullOrWhiteSpace(options.InitialApplicationStatus)
            ? null
            : await ResolveLookupIdAsync(connection, transaction, "ApplicationStatusTypes", options.InitialApplicationStatus, cancellationToken);

        const string insertApplication = """
            INSERT INTO dbo.ApplicationLetters
            (
                Code, ReferenceNo, Name, ContactPerson, MainContactNo, ApplicationDate, DateReceived,
                Comments, Programme, Requirements, ApplicationStatus, UserName, DateCreated, DateUpdated,
                AuditID, CreatedBy, MessageID
            )
            OUTPUT INSERTED.ID
            VALUES
            (
                @Code, @ReferenceNo, @Name, @ContactPerson, @MainContactNo, @ApplicationDate, @DateReceived,
                @Comments, @Programme, @Requirements, @ApplicationStatus, @UserName, @DateCreated, @DateUpdated,
                @AuditID, @CreatedBy, @MessageID
            );
            """;

        await using SqlCommand command = new(insertApplication, connection, transaction);
        AddNullable(command, "@Code", submission.PublicReference, SqlDbType.NVarChar, 500);
        AddNullable(command, "@ReferenceNo", submission.PublicReference, SqlDbType.NVarChar, 500);
        AddNullable(command, "@Name", submission.OrganizationName, SqlDbType.VarChar, 400);
        AddNullable(command, "@ContactPerson", submission.OrganizationName, SqlDbType.NVarChar, 255);
        AddNullable(command, "@MainContactNo", submission.OrganizationPhone, SqlDbType.NVarChar, 255);
        command.Parameters.Add("@ApplicationDate", SqlDbType.DateTime).Value = submission.SubmittedUtc.UtcDateTime;
        command.Parameters.Add("@DateReceived", SqlDbType.DateTime).Value = submission.SubmittedUtc.UtcDateTime;
        AddNullable(command, "@Comments", CombineComments(submission), SqlDbType.NVarChar, 2500);
        AddNullable(command, "@Programme", submission.ProgramCategory, SqlDbType.NVarChar, 255);
        AddNullable(command, "@Requirements", CombineRequirements(submission), SqlDbType.NVarChar, 255);
        command.Parameters.Add("@ApplicationStatus", SqlDbType.BigInt).Value = statusId.HasValue ? statusId.Value : DBNull.Value;
        AddNullable(command, "@UserName", options.UserName, SqlDbType.NVarChar, 50);
        command.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = submission.SubmittedUtc.UtcDateTime;
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = submission.SubmittedUtc.UtcDateTime;
        AddNullable(command, "@AuditID", $"RequestPortal:{submission.PortalRequestId:D}", SqlDbType.NVarChar, 100);
        AddNullable(command, "@CreatedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
        command.Parameters.Add("@MessageID", SqlDbType.UniqueIdentifier).Value = submission.PortalRequestId;

        object? inserted = await command.ExecuteScalarAsync(cancellationToken);
        long applicationId = Convert.ToInt64(inserted, System.Globalization.CultureInfo.InvariantCulture);

        foreach (FfpManagerApplicationItem item in submission.Items)
        {
            await InsertItemAsync(connection, transaction, applicationId, item, submission.SubmittedUtc, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new FfpManagerApplicationReference(
            applicationId.ToString(),
            submission.PublicReference,
            submission.SubmittedUtc,
            AlreadyExisted: false);
    }

    public async Task SaveArtifactAsync(
        string applicationId,
        FfpManagerArtifactDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        long id = ParseApplicationId(applicationId);
        if (string.IsNullOrWhiteSpace(options.SupportingDocumentRoot))
        {
            throw new InvalidOperationException("Bridge:SupportingDocumentRoot must be configured before artifacts can be written to FFP Manager.");
        }

        string safeName = SanitizeFileName(descriptor.FileName);
        string finalPath = Path.Combine(
            options.SupportingDocumentRoot,
            $"ApplicationLetter-{id}-{descriptor.PortalDocumentId:N}-{safeName}");
        string? directory = Path.GetDirectoryName(finalPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream file = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(file, cancellationToken);
                await file.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, finalPath, overwrite: true);

            await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
            await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            long documentTypeId = await ResolveDocumentTypeIdAsync(connection, transaction, descriptor.DocumentType, cancellationToken);

            const string upsert = """
                IF EXISTS (SELECT 1 FROM dbo.SupportingDocument WHERE Oid = @Oid AND ApplicationLetter = @ApplicationLetter)
                BEGIN
                    UPDATE dbo.SupportingDocument
                    SET DocumentNo = @DocumentNo,
                        DocumentType = @DocumentType,
                        Description = @Description,
                        DateSubmitted = @DateSubmitted,
                        SubmittedBy = @SubmittedBy,
                        UserName = @UserName,
                        DateUpdated = @DateUpdated,
                        FileName = @FileName,
                        CreatedBy = @CreatedBy
                    WHERE Oid = @Oid AND ApplicationLetter = @ApplicationLetter;
                END
                ELSE
                BEGIN
                    INSERT INTO dbo.SupportingDocument
                    (
                        Oid, DateCreated, DocumentNo, DocumentType, Description, DateSubmitted,
                        SubmittedBy, UserName, DateUpdated, ApplicationLetter, FileName, CreatedBy
                    )
                    VALUES
                    (
                        @Oid, @DateCreated, @DocumentNo, @DocumentType, @Description, @DateSubmitted,
                        @SubmittedBy, @UserName, @DateUpdated, @ApplicationLetter, @FileName, @CreatedBy
                    );
                END
                """;

            await using SqlCommand command = new(upsert, connection, transaction);
            command.Parameters.Add("@Oid", SqlDbType.UniqueIdentifier).Value = descriptor.PortalDocumentId;
            command.Parameters.Add("@ApplicationLetter", SqlDbType.BigInt).Value = id;
            AddNullable(command, "@DocumentNo", $"ApplicationLetter-{id}", SqlDbType.NVarChar, 100);
            command.Parameters.Add("@DocumentType", SqlDbType.BigInt).Value = documentTypeId;
            AddNullable(command, "@Description", descriptor.DocumentType, SqlDbType.NVarChar, 100);
            command.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = descriptor.UploadedUtc.UtcDateTime;
            command.Parameters.Add("@DateSubmitted", SqlDbType.DateTime).Value = descriptor.UploadedUtc.UtcDateTime;
            AddNullable(command, "@SubmittedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
            AddNullable(command, "@UserName", options.UserName, SqlDbType.NVarChar, 50);
            command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = DateTime.UtcNow;
            AddNullable(command, "@FileName", finalPath, SqlDbType.NVarChar, 1024);
            AddNullable(command, "@CreatedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            throw;
        }
    }

    public async Task<FfpManagerApplicationStatus?> GetStatusAsync(
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        long id = ParseApplicationId(applicationId);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT al.ID,
                   al.DateCreated,
                   al.DateUpdated,
                   ast.Code,
                   ast.Name,
                   ast.Description
            FROM dbo.ApplicationLetters al
            LEFT JOIN dbo.ApplicationStatusTypes ast ON ast.ID = al.ApplicationStatus
            WHERE al.ID = @ID AND al.GCRecord IS NULL;
            """;

        await using SqlCommand command = new(sql, connection);
        command.Parameters.Add("@ID", SqlDbType.BigInt).Value = id;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        string rawCode = reader.IsDBNull(3) ? "RECEIVED" : reader.GetString(3);
        string statusText = reader.IsDBNull(4) ? rawCode : reader.GetString(4);
        string canonical = MapStatus(rawCode, statusText);
        DateTime changed = !reader.IsDBNull(2)
            ? reader.GetDateTime(2)
            : !reader.IsDBNull(1) ? reader.GetDateTime(1) : DateTime.UtcNow;
        string? detail = reader.IsDBNull(5) ? null : reader.GetString(5);
        bool terminal = canonical is "REJECTED" or "DECLINED" or "CLOSED";
        return new FfpManagerApplicationStatus(id.ToString(), canonical, statusText, new DateTimeOffset(DateTime.SpecifyKind(changed, DateTimeKind.Utc)), detail, terminal);
    }

    public async Task SaveInformationResponseAsync(
        FfpManagerInformationResponse response,
        CancellationToken cancellationToken = default)
    {
        long id = ParseApplicationId(response.ApplicationId);
        string code = response.MessageId.ToString("N")[..25];
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            IF NOT EXISTS
            (
                SELECT 1
                FROM dbo.ApplicationNotes
                WHERE ApplicationLetterID = @ApplicationLetterID
                  AND Code = @Code
                  AND NoteType = @NoteType
            )
            BEGIN
                INSERT INTO dbo.ApplicationNotes
                (
                    DateCreated, Code, Name, Description, UserName, DateUpdated,
                    ApplicationLetterID, NoteType, CreatedBy
                )
                VALUES
                (
                    @DateCreated, @Code, @Name, @Description, @UserName, @DateUpdated,
                    @ApplicationLetterID, @NoteType, @CreatedBy
                );
            END
            """;

        await using SqlCommand command = new(sql, connection);
        command.Parameters.Add("@ApplicationLetterID", SqlDbType.BigInt).Value = id;
        AddNullable(command, "@Code", code, SqlDbType.NVarChar, 25);
        AddNullable(command, "@Name", "RequestPortal response", SqlDbType.NVarChar, 255);
        AddNullable(command, "@Description", response.Message, SqlDbType.NVarChar, -1);
        AddNullable(command, "@UserName", options.UserName, SqlDbType.NVarChar, 50);
        command.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = response.RespondedUtc.UtcDateTime;
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = response.RespondedUtc.UtcDateTime;
        AddNullable(command, "@NoteType", options.InformationResponseNoteType, SqlDbType.NVarChar, 500);
        AddNullable(command, "@CreatedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<FfpManagerApplicationSubmission?> GetApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        long id = ParseApplicationId(applicationId);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT ID, Code, ReferenceNo, Name, MainContactNo, ApplicationDate, DateCreated,
                   Comments, Programme, Requirements, MessageID
            FROM dbo.ApplicationLetters
            WHERE ID = @ID AND GCRecord IS NULL;
            """;
        await using SqlCommand command = new(sql, connection);
        command.Parameters.Add("@ID", SqlDbType.BigInt).Value = id;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        Guid portalRequestId = reader.IsDBNull(10) ? Guid.Empty : reader.GetGuid(10);
        string publicReference = reader.IsDBNull(1)
            ? reader.IsDBNull(2) ? id.ToString() : reader.GetString(2)
            : reader.GetString(1);
        string organizationName = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        string? phone = reader.IsDBNull(4) ? null : reader.GetString(4);
        DateTime submitted = !reader.IsDBNull(5)
            ? reader.GetDateTime(5)
            : !reader.IsDBNull(6) ? reader.GetDateTime(6) : DateTime.UtcNow;
        string? comments = reader.IsDBNull(7) ? null : reader.GetString(7);
        string? programme = reader.IsDBNull(8) ? null : reader.GetString(8);
        string? requirements = reader.IsDBNull(9) ? null : reader.GetString(9);
        await reader.CloseAsync();

        List<FfpManagerApplicationItem> items = await ReadItemsAsync(connection, id, cancellationToken);
        return new FfpManagerApplicationSubmission(
            portalRequestId,
            portalRequestId,
            publicReference,
            Guid.Empty,
            organizationName,
            null,
            null,
            phone,
            null,
            Guid.Empty,
            new DateTimeOffset(DateTime.SpecifyKind(submitted, DateTimeKind.Utc)),
            "Unknown",
            "Normal",
            comments,
            requirements,
            null,
            programme,
            null,
            comments,
            items,
            []);
    }

    public async Task<FfpManagerApplicationStatus> SetStatusAsync(
        string applicationId,
        string statusCode,
        string statusText,
        string? detail,
        bool terminal,
        CancellationToken cancellationToken = default)
    {
        long id = ParseApplicationId(applicationId);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        long statusId = await ResolveLookupIdAsync(
            connection,
            transaction,
            "ApplicationStatusTypes",
            string.IsNullOrWhiteSpace(statusCode) ? statusText : statusCode,
            cancellationToken);
        const string sql = "UPDATE dbo.ApplicationLetters SET ApplicationStatus = @Status, DateUpdated = @DateUpdated WHERE ID = @ID;";
        await using SqlCommand command = new(sql, connection, transaction);
        command.Parameters.Add("@Status", SqlDbType.BigInt).Value = statusId;
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = DateTime.UtcNow;
        command.Parameters.Add("@ID", SqlDbType.BigInt).Value = id;
        int affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new KeyNotFoundException($"FFP Manager application '{applicationId}' was not found.");
        }
        await transaction.CommitAsync(cancellationToken);
        return await GetStatusAsync(applicationId, cancellationToken)
            ?? throw new KeyNotFoundException($"FFP Manager application '{applicationId}' was not found after status update.");
    }

    private async Task InsertItemAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long applicationId,
        FfpManagerApplicationItem item,
        DateTimeOffset submittedUtc,
        CancellationToken cancellationToken)
    {
        if (item.Quantity != decimal.Truncate(item.Quantity) || item.Quantity < int.MinValue || item.Quantity > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"FFP Manager ItemsRequested.Quantity is an integer; portal item {item.PortalItemId:D} has quantity {item.Quantity}.");
        }

        long unitId = await ResolveLookupIdAsync(connection, transaction, "UnitsOfMeasure", item.UnitOfMeasure, cancellationToken);
        const string sql = """
            INSERT INTO dbo.ItemsRequested
            (
                Code, Name, Description, UserName, DateCreated, DateUpdated,
                Quantity, ApplicationLetterID, UnitOfMeasure, CreatedBy
            )
            VALUES
            (
                @Code, @Name, @Description, @UserName, @DateCreated, @DateUpdated,
                @Quantity, @ApplicationLetterID, @UnitOfMeasure, @CreatedBy
            );
            """;
        await using SqlCommand command = new(sql, connection, transaction);
        AddNullable(command, "@Code", item.PortalItemId.ToString("N")[..25], SqlDbType.NVarChar, 25);
        AddNullable(command, "@Name", Truncate(item.Description, 255), SqlDbType.NVarChar, 255);
        AddNullable(command, "@Description", item.Description, SqlDbType.NVarChar, 500);
        AddNullable(command, "@UserName", options.UserName, SqlDbType.NVarChar, 50);
        command.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = submittedUtc.UtcDateTime;
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = submittedUtc.UtcDateTime;
        command.Parameters.Add("@Quantity", SqlDbType.Int).Value = decimal.ToInt32(item.Quantity);
        command.Parameters.Add("@ApplicationLetterID", SqlDbType.BigInt).Value = applicationId;
        command.Parameters.Add("@UnitOfMeasure", SqlDbType.BigInt).Value = unitId;
        AddNullable(command, "@CreatedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<FfpManagerApplicationItem>> ReadItemsAsync(
        SqlConnection connection,
        long applicationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ir.Code, ir.Description, ir.Quantity, COALESCE(u.Code, u.Name, CONVERT(nvarchar(30), ir.UnitOfMeasure))
            FROM dbo.ItemsRequested ir
            LEFT JOIN dbo.UnitsOfMeasure u ON u.ID = ir.UnitOfMeasure
            WHERE ir.ApplicationLetterID = @ApplicationLetterID;
            """;
        await using SqlCommand command = new(sql, connection);
        command.Parameters.Add("@ApplicationLetterID", SqlDbType.BigInt).Value = applicationId;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        List<FfpManagerApplicationItem> result = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            Guid portalItemId = Guid.TryParseExact(reader.IsDBNull(0) ? null : reader.GetString(0), "N", out Guid parsed)
                ? parsed
                : Guid.Empty;
            result.Add(new FfpManagerApplicationItem(
                portalItemId,
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3)));
        }
        return result;
    }

    private async Task<long> ResolveDocumentTypeIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string portalDocumentType,
        CancellationToken cancellationToken)
    {
        string lookup = options.DocumentTypeMap.TryGetValue(portalDocumentType, out string? mapped) && !string.IsNullOrWhiteSpace(mapped)
            ? mapped
            : portalDocumentType;
        try
        {
            return await ResolveLookupIdAsync(connection, transaction, "DocumentTypes", lookup, cancellationToken);
        }
        catch (InvalidOperationException) when (!string.IsNullOrWhiteSpace(options.DefaultDocumentType))
        {
            return await ResolveLookupIdAsync(connection, transaction, "DocumentTypes", options.DefaultDocumentType, cancellationToken);
        }
    }

    private static async Task<long> ResolveLookupIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string table,
        string codeOrName,
        CancellationToken cancellationToken)
    {
        if (table is not ("UnitsOfMeasure" or "DocumentTypes" or "ApplicationStatusTypes"))
        {
            throw new ArgumentOutOfRangeException(nameof(table));
        }

        string sql = $"SELECT TOP (2) ID FROM dbo.[{table}] WHERE Code = @Value OR Name = @Value ORDER BY CASE WHEN Code = @Value THEN 0 ELSE 1 END, ID;";
        await using SqlCommand command = new(sql, connection, transaction);
        command.Parameters.Add("@Value", SqlDbType.NVarChar, 255).Value = codeOrName;
        List<long> ids = [];
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(0));
        }
        if (ids.Count == 0)
        {
            throw new InvalidOperationException($"FFP Manager lookup dbo.{table} contains no Code/Name matching '{codeOrName}'.");
        }
        return ids[0];
    }

    private async Task<ExistingApplication?> FindByPortalRequestIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid portalRequestId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) ID, Code, ReferenceNo, DateCreated
            FROM dbo.ApplicationLetters WITH (UPDLOCK, HOLDLOCK)
            WHERE MessageID = @MessageID AND GCRecord IS NULL
            ORDER BY ID;
            """;
        await using SqlCommand command = new(sql, connection, transaction);
        command.Parameters.Add("@MessageID", SqlDbType.UniqueIdentifier).Value = portalRequestId;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new ExistingApplication(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)));
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SqlConnectionString))
        {
            throw new InvalidOperationException("Bridge:SqlConnectionString is required when Bridge:Gateway is Sql.");
        }
        SqlConnection connection = new(options.SqlConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private string MapStatus(string rawCode, string rawName)
    {
        if (options.StatusMap.TryGetValue(rawCode, out string? mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return NormalizeStatus(mapped);
        }
        if (options.StatusMap.TryGetValue(rawName, out mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return NormalizeStatus(mapped);
        }
        return NormalizeStatus(string.IsNullOrWhiteSpace(rawCode) ? rawName : rawCode);
    }

    private static string NormalizeStatus(string value) =>
        value.Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();

    private static long ParseApplicationId(string applicationId) =>
        long.TryParse(applicationId, out long id) && id > 0
            ? id
            : throw new ArgumentException($"'{applicationId}' is not a valid FFP Manager ApplicationLetters.ID.", nameof(applicationId));

    private static string SanitizeFileName(string value)
    {
        string name = Path.GetFileName(value);
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        return string.IsNullOrWhiteSpace(name) ? "document.bin" : name;
    }

    private static string? CombineComments(FfpManagerApplicationSubmission submission)
    {
        string[] parts = [submission.Summary ?? string.Empty, submission.Circumstances ?? string.Empty, submission.OrganizationAddress ?? string.Empty, submission.OrganizationEmail ?? string.Empty];
        string value = string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return Truncate(value, 2500);
    }

    private static string? CombineRequirements(FfpManagerApplicationSubmission submission)
    {
        string value = string.Join(" / ", new[] { submission.Category, submission.Subcategory }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return Truncate(value, 255);
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxLength ? value : value[..maxLength];

    private static void AddNullable(SqlCommand command, string name, string? value, SqlDbType type, int size)
    {
        SqlParameter parameter = size == -1 ? command.Parameters.Add(name, type, -1) : command.Parameters.Add(name, type, size);
        parameter.Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
    }

    private sealed record ExistingApplication(long Id, string? Code, string? ReferenceNo, DateTimeOffset? DateCreated);
}
