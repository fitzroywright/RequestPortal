using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public sealed partial class SqlFfpManagerGateway : IFfpManagerGateway
{
    // This lookup never repairs references, inserts applications or changes destination data.
    public async Task<ExternalApplicationReceipt?> FindApplicationAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT TOP (2) ID, ReferenceNo, DateCreated, GCRecord FROM dbo.ApplicationLetters
            WHERE ExternalRequestId=@RequestId ORDER BY ID
            """, connection);
        command.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = requestId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        if (!reader.IsDBNull(3)) throw new IntegrationTransferException(TransferFailures.For("DestinationLookup", "DESTINATION_CONFLICT"));
        if (reader.IsDBNull(1) || string.IsNullOrWhiteSpace(reader.GetString(1)))
            throw new IntegrationTransferException(TransferFailures.For("DestinationLookup", "DESTINATION_CONFLICT"));
        var receipt = new ExternalApplicationReceipt(requestId, reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(1), reader.IsDBNull(2) ? DateTimeOffset.UnixEpoch : JamaicaBusinessTime.ToUtc(reader.GetDateTime(2)),
            "JMDB01", FfpSqlConnection.DatabaseFor(options.SqlTarget));
        if (await reader.ReadAsync(cancellationToken))
            throw new IntegrationTransferException(TransferFailures.For("DestinationLookup", "DESTINATION_CONFLICT"));
        return receipt;
    }
    private readonly BridgeOptions options;

    public SqlFfpManagerGateway(IOptions<BridgeOptions> options)
    {
        this.options = options.Value;
    }

    public async Task<ExternalApplicationReference> CreateApplicationAsync(
        ExternalApplicationSubmission submission,
        CancellationToken cancellationToken = default)
    {
        BridgeApplicantName.Validate(submission);
        BridgeApplicantAddress.Validate(submission);
        BridgeApplicantProfile.Validate(submission);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        ExistingApplication? existing = await FindByPortalRequestIdAsync(connection, transaction, submission.PortalRequestId, cancellationToken);
        if (existing is not null)
        {
            if (string.IsNullOrWhiteSpace(existing.ReferenceNo) || string.Equals(existing.ReferenceNo, submission.PublicReference, StringComparison.OrdinalIgnoreCase))
                throw new IntegrationTransferException(TransferFailures.For("DestinationLookup", "DESTINATION_CONFLICT"));
            string existingReference = existing.ReferenceNo;
            await transaction.CommitAsync(cancellationToken);
            return new ExternalApplicationReference(
                existing.Id.ToString(),
                existingReference,
                existing.DateCreated ?? submission.SubmittedUtc,
                AlreadyExisted: true);
        }

        var applicantProfile = await BridgeApplicantProfile.PrepareAsync(connection, transaction, submission, cancellationToken);

        var generatedReference = await FfpManagerReferenceNumberGenerator.GenerateAsync(
            connection, transaction, submission.Parish, submission.SubmittedUtc, cancellationToken);

        long? statusId = string.IsNullOrWhiteSpace(options.InitialApplicationStatus)
            ? null
            : await ResolveLookupIdAsync(connection, transaction, "ApplicationStatusTypes", options.InitialApplicationStatus, cancellationToken);

        string insertApplication = $$"""
            INSERT INTO dbo.ApplicationLetters
            (
                Code, ReferenceNo, Name, ContactPerson, FirstName, LastName, Street, Town, DateOfBirth, Gender, Occupation, MainContactNo, ApplicationDate, DateReceived,
                Programme, Requirements, ApplicationStatus, UserName, DateCreated, DateUpdated,
                AuditID, CreatedBy, ExternalRequestId, Parish, DocumentSource{{BridgeQuickIntakeFields.Columns(submission)}}
            )
            OUTPUT INSERTED.ID
            VALUES
            (
                @Code, @ReferenceNo, @Name, @ContactPerson, @FirstName, @LastName, @Street, @Town, @DateOfBirth, @Gender, @Occupation, @MainContactNo, @ApplicationDate, @DateReceived,
                @Programme, @Requirements, @ApplicationStatus, @UserName, @DateCreated, @DateUpdated,
                @AuditID, @CreatedBy, @ExternalRequestId, @Parish, @DocumentSource{{BridgeQuickIntakeFields.Values(submission)}}
            );
            """;

        await using SqlCommand command = new(insertApplication, connection, transaction);
        AddNullable(command, "@Code", submission.PublicReference, SqlDbType.NVarChar, 500);
        AddNullable(command, "@ReferenceNo", generatedReference.ReferenceNo, SqlDbType.NVarChar, 500);
        AddNullable(command, "@Name", submission.OrganizationName, SqlDbType.VarChar, 400);
        AddNullable(command, "@ContactPerson", submission.OrganizationName, SqlDbType.NVarChar, 255);
        AddNullable(command, "@FirstName", submission.FirstName, SqlDbType.NVarChar, 200);
        AddNullable(command, "@LastName", submission.LastName, SqlDbType.NVarChar, 100);
        AddNullable(command, "@Street", submission.StreetAddress, SqlDbType.NVarChar, 500);
        AddNullable(command, "@Town", submission.CommunityDistrict, SqlDbType.NVarChar, 500);
        command.Parameters.Add("@DateOfBirth", SqlDbType.DateTime).Value = submission.DateOfBirth is { } birth ? birth.ToDateTime(TimeOnly.MinValue) : DBNull.Value;
        command.Parameters.Add("@Gender", SqlDbType.Int).Value = BridgeApplicantProfile.Gender(submission.ApplicantGender) is { } gender ? gender : DBNull.Value;
        BridgeApplicationReceipt.AddParameters(command, submission);
        AddNullable(command, "@MainContactNo", BridgeApplicantProfile.MainContact(submission), SqlDbType.NVarChar, 255);
        BridgeQuickIntakeFields.AddParameters(command, submission);
        AddNullable(command, "@Requirements", CombineRequirements(submission), SqlDbType.NVarChar, 255);
        command.Parameters.Add("@ApplicationStatus", SqlDbType.BigInt).Value = statusId.HasValue ? statusId.Value : DBNull.Value;
        AddNullable(command, "@UserName", options.UserName, SqlDbType.NVarChar, 50);
        command.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(submission.SubmittedUtc);
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(submission.SubmittedUtc);
        AddNullable(command, "@AuditID", $"RequestPortal:{submission.PortalRequestId:D}", SqlDbType.NVarChar, 100);
        AddNullable(command, "@CreatedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
        command.Parameters.Add("@ExternalRequestId", SqlDbType.UniqueIdentifier).Value = submission.PortalRequestId;

        command.Parameters.Add("@Parish", SqlDbType.BigInt).Value = generatedReference.ParishId;
        AddNullable(command, "@DocumentSource", submission.DocumentSource, SqlDbType.NVarChar, 255);
        object? inserted = await command.ExecuteScalarAsync(cancellationToken);
        long applicationId = Convert.ToInt64(inserted, System.Globalization.CultureInfo.InvariantCulture);

        // Commit the intake note with its new application; retries reuse the existing application above.
        await using (SqlCommand note = new("""
            INSERT INTO dbo.ApplicationNotes
                (DateCreated, Description, UserName, DateUpdated, ApplicationLetterID, CreatedBy)
            VALUES
                (@Now, @Description, @UserName, @Now, @ApplicationLetterID, @CreatedBy);
            """, connection, transaction))
        {
            note.Parameters.Add("@Now", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(DateTimeOffset.UtcNow);
            AddNullable(note, "@Description", CombineComments(submission), SqlDbType.NVarChar, 2500);
            AddNullable(note, "@UserName", options.UserName, SqlDbType.NVarChar, 50);
            AddNullable(note, "@CreatedBy", options.UserName, SqlDbType.NVarChar, 100);
            note.Parameters.Add("@ApplicationLetterID", SqlDbType.BigInt).Value = applicationId;
            await note.ExecuteNonQueryAsync(cancellationToken);
        }

        await BridgeApplicantProfile.InsertChildrenAsync(connection, transaction, applicationId, applicantProfile, submission, options, cancellationToken);

        foreach (ExternalApplicationItem item in submission.Items)
        {
            await InsertItemAsync(connection, transaction, applicationId, item, submission.SubmittedUtc, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ExternalApplicationReference(
            applicationId.ToString(),
            generatedReference.ReferenceNo,
            submission.SubmittedUtc,
            AlreadyExisted: false);
    }

    public async Task SaveArtifactAsync(
        string applicationId,
        ExternalArtifactDescriptor descriptor,
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
        string managerPath = BridgeDocumentPath.ForManager(options, finalPath);
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
            command.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(descriptor.UploadedUtc);
            command.Parameters.Add("@DateSubmitted", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(descriptor.UploadedUtc);
            AddNullable(command, "@SubmittedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
            AddNullable(command, "@UserName", options.UserName, SqlDbType.NVarChar, 50);
            command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(DateTimeOffset.UtcNow);
            AddNullable(command, "@FileName", managerPath, SqlDbType.NVarChar, 1024);
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

    public async Task<ExternalApplicationStatus?> GetStatusAsync(
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

        string rawCode = reader.IsDBNull(3) ? "" : reader.GetString(3);
        string statusText = reader.IsDBNull(4) ? rawCode : reader.GetString(4);
        DateTime? changed = !reader.IsDBNull(2)
            ? reader.GetDateTime(2)
            : !reader.IsDBNull(1) ? reader.GetDateTime(1) : null;
        string? detail = reader.IsDBNull(5) ? null : reader.GetString(5);
        bool terminal = rawCode.Trim().Equals("Complete", StringComparison.OrdinalIgnoreCase)
            || rawCode.Trim().Equals("Unable to Assist", StringComparison.OrdinalIgnoreCase)
            || rawCode.Trim().Equals("No Longer In Need", StringComparison.OrdinalIgnoreCase);
        return new ExternalApplicationStatus(id.ToString(), rawCode, statusText, changed.HasValue ? JamaicaBusinessTime.ToUtc(changed.Value) : DateTimeOffset.UtcNow, detail, terminal);
    }

    public async Task SaveInformationResponseAsync(
        ExternalInformationResponse response,
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
        command.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(response.RespondedUtc);
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(response.RespondedUtc);
        AddNullable(command, "@NoteType", options.InformationResponseNoteType, SqlDbType.NVarChar, 500);
        AddNullable(command, "@CreatedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ExternalApplicationSubmission?> GetApplicationAsync(
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        long id = ParseApplicationId(applicationId);
        await using SqlConnection connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT ID, Code, ReferenceNo, Name, MainContactNo, COALESCE(DateReceived, ApplicationDate) AS SubmittedDate, DateCreated,
                   Comments, Programme, Requirements, ExternalRequestId, Street, Town, FirstName, LastName, DateOfBirth, Gender, Occupation
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
        DateTime? submitted = !reader.IsDBNull(5)
            ? reader.GetDateTime(5)
            : !reader.IsDBNull(6) ? reader.GetDateTime(6) : null;
        string? comments = reader.IsDBNull(7) ? null : reader.GetString(7);
        string? programme = reader.IsDBNull(8) ? null : reader.GetString(8);
        string? requirements = reader.IsDBNull(9) ? null : reader.GetString(9);
        string? streetAddress = reader.IsDBNull(11) ? null : reader.GetString(11);
        string? communityDistrict = reader.IsDBNull(12) ? null : reader.GetString(12);
        string? firstName = reader.IsDBNull(13) ? null : reader.GetString(13);
        string? lastName = reader.IsDBNull(14) ? null : reader.GetString(14);
        DateOnly? birth = reader.IsDBNull(15) ? null : DateOnly.FromDateTime(reader.GetDateTime(15));
        string? gender = reader.IsDBNull(16) ? null : reader.GetInt32(16) switch { 1 => "Male", 2 => "Female", 0 => "Unspecified", _ => null };
        string? occupation = reader.IsDBNull(17) ? null : reader.GetString(17);
        await reader.CloseAsync();
        string? primary = null, secondary = null, email = null;
        await using (var children = new SqlCommand("SELECT NumberType,Number FROM dbo.PhoneNumbers WHERE ApplicationLetter=@ID AND GCRecord IS NULL AND NumberType IN (93,83) ORDER BY OID; SELECT TOP (1) MailboxName FROM dbo.EmailAddresses WHERE ApplicationLetter=@ID AND GCRecord IS NULL AND AddressType=93 ORDER BY OID;", connection))
        {
            children.Parameters.Add("@ID", SqlDbType.BigInt).Value = id;
            await using var childReader = await children.ExecuteReaderAsync(cancellationToken);
            while (await childReader.ReadAsync(cancellationToken))
            {
                if (childReader.IsDBNull(1)) continue;
                if (childReader.GetInt64(0) == 93) primary ??= childReader.GetString(1);
                else secondary ??= childReader.GetString(1);
            }
            await childReader.NextResultAsync(cancellationToken);
            if (await childReader.ReadAsync(cancellationToken) && !childReader.IsDBNull(0)) email = childReader.GetString(0);
        }

        List<ExternalApplicationItem> items = await ReadItemsAsync(connection, id, cancellationToken);
        return new ExternalApplicationSubmission(
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
            submitted.HasValue ? JamaicaBusinessTime.ToUtc(submitted.Value) : DateTimeOffset.UtcNow,
            "Unknown",
            "Normal",
            comments,
            requirements,
            null,
            programme,
            null,
            comments,
            items,
            [],
            StreetAddress: streetAddress,
            CommunityDistrict: communityDistrict,
            FirstName: firstName,
            LastName: lastName,
            ApplicantEmail: email,
            ApplicantEmploymentStatus: occupation,
            ApplicantGender: gender,
            PrimaryTelephone: primary,
            SecondaryTelephone: secondary,
            DateOfBirth: birth);
    }

    public async Task<ExternalApplicationStatus> SetStatusAsync(
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
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(DateTimeOffset.UtcNow);
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
        ExternalApplicationItem item,
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
        command.Parameters.Add("@DateCreated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(submittedUtc);
        command.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(submittedUtc);
        command.Parameters.Add("@Quantity", SqlDbType.Int).Value = decimal.ToInt32(item.Quantity);
        command.Parameters.Add("@ApplicationLetterID", SqlDbType.BigInt).Value = applicationId;
        command.Parameters.Add("@UnitOfMeasure", SqlDbType.BigInt).Value = unitId;
        AddNullable(command, "@CreatedBy", options.CreatedBy, SqlDbType.NVarChar, 100);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<ExternalApplicationItem>> ReadItemsAsync(
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
        List<ExternalApplicationItem> result = [];
        while (await reader.ReadAsync(cancellationToken))
        {
            Guid portalItemId = Guid.TryParseExact(reader.IsDBNull(0) ? null : reader.GetString(0), "N", out Guid parsed)
                ? parsed
                : Guid.Empty;
            result.Add(new ExternalApplicationItem(
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
            SELECT TOP (2) ID, Code, ReferenceNo, DateCreated, GCRecord
            FROM dbo.ApplicationLetters WITH (UPDLOCK, HOLDLOCK)
            WHERE ExternalRequestId = @ExternalRequestId
            ORDER BY ID;
            """;
        await using SqlCommand command = new(sql, connection, transaction);
        command.Parameters.Add("@ExternalRequestId", SqlDbType.UniqueIdentifier).Value = portalRequestId;
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        if (!reader.IsDBNull(4)) throw new IntegrationTransferException(TransferFailures.For("DestinationLookup", "DESTINATION_CONFLICT"));
        var existing = new ExistingApplication(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : JamaicaBusinessTime.ToUtc(reader.GetDateTime(3)));
        if (await reader.ReadAsync(cancellationToken)) throw new IntegrationTransferException(TransferFailures.For("DestinationLookup", "DESTINATION_CONFLICT"));
        return existing;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SqlConnectionString))
        {
            throw new InvalidOperationException("Bridge:SqlConnectionString is required when Bridge:Gateway is Sql.");
        }
        return await FfpSqlConnection.OpenAsync(options.SqlConnectionString, options.SqlTarget, cancellationToken);
    }

    private static long ParseApplicationId(string applicationId) =>
        long.TryParse(applicationId, out long id) && id > 0
            ? id
            : throw new ArgumentException($"'{applicationId}' is not a valid FFP Manager ApplicationLetters.ID.", nameof(applicationId));

    private static string SanitizeFileName(string value) => BridgeDocumentPath.SafeFileName(value);

    private static string? CombineComments(ExternalApplicationSubmission submission)
    {
        string[] parts = [submission.Summary ?? string.Empty, submission.Circumstances ?? string.Empty, submission.OrganizationAddress ?? string.Empty, submission.OrganizationEmail ?? string.Empty];
        string value = string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return Truncate(value, 2500);
    }

    private static string? CombineRequirements(ExternalApplicationSubmission submission)
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
