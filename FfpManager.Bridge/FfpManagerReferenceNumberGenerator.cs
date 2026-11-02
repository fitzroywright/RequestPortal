using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

/// <summary>Uses FFP Manager's existing XPO ApplicationLetter counter; never creates a separate sequence.</summary>
public static class FfpManagerReferenceNumberGenerator
{
    public const string SequenceType = "FFP.Module.BusinessObjects.ApplicationLetter";

    public static string Format(string parishCode, DateTimeOffset createdUtc, long number)
    {
        if (string.IsNullOrWhiteSpace(parishCode) || parishCode.Contains('/') || number < 1)
            throw new ArgumentException("A valid parish code and positive sequence number are required.");
        return $"{parishCode.Trim()}/{JamaicaBusinessTime.ToLocal(createdUtc):yy}/GR/{number.ToString("D4", CultureInfo.InvariantCulture)}";
    }

    public static async Task<string> EnsureExistingAsync(
        SqlConnection connection, SqlTransaction transaction, long applicationId,
        string? referenceNo, DateTimeOffset createdUtc, RequestPortal.Application.Integration.ExternalApplicationSubmission submission,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(referenceNo) &&
            !string.Equals(referenceNo, submission.PublicReference, StringComparison.OrdinalIgnoreCase))
            return referenceNo;
        var generated = await GenerateAsync(connection, transaction, submission.Parish, createdUtc, cancellationToken);
        await using SqlCommand update = new(
            "UPDATE dbo.ApplicationLetters SET Code=@Code,ReferenceNo=@ReferenceNo,Parish=@Parish,DocumentSource=@Source,DateUpdated=@DateUpdated WHERE ID=@ID AND ExternalRequestId=@RequestId",
            connection, transaction);
        update.Parameters.Add("@DateUpdated", SqlDbType.DateTime).Value = JamaicaBusinessTime.ToLocal(DateTimeOffset.UtcNow);
        update.Parameters.Add("@ID", SqlDbType.BigInt).Value = applicationId;
        update.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = submission.PortalRequestId;
        update.Parameters.Add("@Code", SqlDbType.NVarChar, 500).Value = submission.PublicReference;
        update.Parameters.Add("@ReferenceNo", SqlDbType.NVarChar, 500).Value = generated.ReferenceNo;
        update.Parameters.Add("@Parish", SqlDbType.BigInt).Value = generated.ParishId;
        update.Parameters.Add("@Source", SqlDbType.NVarChar, 255).Value = submission.DocumentSource;
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("The existing Portal application could not be assigned its FFP reference.");
        return generated.ReferenceNo;
    }

    public static async Task<(long ParishId, string ReferenceNo)> GenerateAsync(
        SqlConnection connection, SqlTransaction transaction, string? parish,
        DateTimeOffset createdUtc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parish))
            throw new ExternalApplicationValidationException(nameof(ExternalApplicationSubmission.Parish),
                "An explicitly selected parish is required to generate the FFP Manager reference number. Supply the saved intake or applicant parish; location and address text cannot be used.");
        await using SqlCommand parishCommand = new(
            "SELECT ID,Code FROM dbo.Parishes WHERE Name=@Parish OR Code=@Parish", connection, transaction);
        parishCommand.Parameters.Add("@Parish", SqlDbType.NVarChar, 255).Value = parish.Trim();
        long parishId;
        string parishCode;
        await using (SqlDataReader reader = await parishCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new ExternalApplicationValidationException(nameof(ExternalApplicationSubmission.Parish),
                    "The submitted parish does not match FFP Manager's parish lookup. Select a valid parish before retrying.");
            parishId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            parishCode = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The submitted parish matches multiple FFP Manager parish records.");
        }

        await using SqlCommand identity = new(
            "SELECT COLUMNPROPERTY(OBJECT_ID('dbo.IDGeneratorTable'),'Oid','IsIdentity')", connection, transaction);
        object? isIdentity = await identity.ExecuteScalarAsync(cancellationToken);
        if (isIdentity is null || isIdentity is DBNull || Convert.ToInt32(isIdentity, CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("FFP Manager IDGeneratorTable.Oid must be the existing mutable XPO sequence counter.");

        // Discover the persisted type discriminator from the existing row instead of assuming a schema version.
        // TABLOCKX also serializes legacy XPO allocations; increment its optimistic lock version below.
        string? typeColumn = null;
        object? prefix = null;
        bool hasPrefix = false;
        bool hasVersion = false;
        long previous = 0;
        int matches = 0;
        await using (SqlCommand sequence = new("SELECT * FROM dbo.IDGeneratorTable WITH (TABLOCKX,HOLDLOCK)", connection, transaction))
        await using (SqlDataReader reader = await sequence.ExecuteReaderAsync(cancellationToken))
        {
            string[] columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            int oidIndex = Array.FindIndex(columns, name => name.Equals("Oid", StringComparison.OrdinalIgnoreCase));
            int prefixIndex = Array.FindIndex(columns, name => name.Equals("Prefix", StringComparison.OrdinalIgnoreCase));
            int versionIndex = Array.FindIndex(columns, name => name.Equals("OptimisticLockField", StringComparison.OrdinalIgnoreCase));
            int gcIndex = Array.FindIndex(columns, name => name.Equals("GCRecord", StringComparison.OrdinalIgnoreCase));
            if (oidIndex < 0 || versionIndex < 0)
                throw new InvalidOperationException("The XPO counter schema must contain Oid and OptimisticLockField.");
            while (await reader.ReadAsync(cancellationToken))
            {
                if (gcIndex >= 0 && !reader.IsDBNull(gcIndex)) continue;
                string? discriminator = columns.FirstOrDefault(name =>
                    reader[name] is string value && string.Equals(value, SequenceType, StringComparison.Ordinal));
                if (discriminator is null) continue;
                if (prefixIndex >= 0 && !columns[prefixIndex].Equals(discriminator, StringComparison.OrdinalIgnoreCase) && !reader.IsDBNull(prefixIndex) &&
                    !string.Equals(Convert.ToString(reader.GetValue(prefixIndex), CultureInfo.InvariantCulture), "", StringComparison.Ordinal))
                    continue; // FFP Manager passes an empty serverPrefix to DistributedIdGeneratorHelper.
                matches++;
                typeColumn = discriminator;
                previous = Convert.ToInt64(reader.GetValue(oidIndex), CultureInfo.InvariantCulture);
                hasPrefix = prefixIndex >= 0 && !columns[prefixIndex].Equals(discriminator, StringComparison.OrdinalIgnoreCase);
                prefix = hasPrefix ? reader.GetValue(prefixIndex) : null;
                hasVersion = versionIndex >= 0;
            }
        }
        if (matches != 1 || typeColumn is null || previous < 0 || !hasVersion)
            throw new InvalidOperationException("Exactly one existing ApplicationLetter XPO counter with an empty server prefix is required. Inspect IDGeneratorTable on the test database; do not create a separate counter.");

        string quote = "[" + typeColumn.Replace("]", "]]", StringComparison.Ordinal) + "]";
        string prefixPredicate = !hasPrefix ? "" : prefix is DBNull ? " AND [Prefix] IS NULL" : " AND [Prefix]=@Prefix";
        string sql = $"UPDATE dbo.IDGeneratorTable SET [Oid]=[Oid]+1,[OptimisticLockField]=ISNULL([OptimisticLockField],0)+1 OUTPUT INSERTED.[Oid] WHERE {quote}=@SequenceType AND [Oid]=@Previous{prefixPredicate}";
        await using SqlCommand update = new(sql, connection, transaction);
        update.Parameters.Add("@SequenceType", SqlDbType.NVarChar, 255).Value = SequenceType;
        update.Parameters.Add("@Previous", SqlDbType.BigInt).Value = previous;
        if (hasPrefix && prefix is not DBNull) update.Parameters.AddWithValue("@Prefix", prefix!);
        long next;
        await using (SqlDataReader reader = await update.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The shared FFP Manager counter could not be incremented.");
            next = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The shared FFP Manager counter is ambiguous; the transaction must roll back.");
        }
        string reference = Format(parishCode, createdUtc, next);
        await using SqlCommand duplicate = new("SELECT COUNT(*) FROM dbo.ApplicationLetters WHERE ReferenceNo=@ReferenceNo", connection, transaction);
        duplicate.Parameters.Add("@ReferenceNo", SqlDbType.NVarChar, 500).Value = reference;
        if (Convert.ToInt32(await duplicate.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("The shared FFP counter generated an existing reference number; reconcile the test sequence before retrying.");
        return (parishId, reference);
    }
}
