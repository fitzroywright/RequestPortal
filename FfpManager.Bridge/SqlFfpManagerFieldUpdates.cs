using System.Data;
using Microsoft.Data.SqlClient;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public sealed partial class SqlFfpManagerGateway
{
    public async Task<ApplicationFieldSnapshot> ReadFieldsAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        if (requestId == Guid.Empty) throw new InvalidOperationException("An immutable request ID is required.");
        if (options.SqlTarget != "Production") throw new InvalidOperationException("Historical field updates require the verified FFPJM target.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await PrepareFieldSessionAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await DemandFieldPermissionsAsync(connection, transaction, cancellationToken);
        var snapshot = await ReadFieldSnapshotAsync(connection, transaction, requestId, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return snapshot;
    }

    public async Task<ApplicationFieldSnapshot> UpdateFieldsAsync(ApplicationFieldUpdate update, CancellationToken cancellationToken = default)
    {
        var proposed = QuickIntakeApplicationFields.MappedValues(update.Source);
        var preview = new ApplicationFieldPreview(update.Source.PortalRequestId, update.Source.PublicReference, update.Expected, proposed);
        preview.ValidateApproval(update.ApprovedFields);
        if (options.SqlTarget != "Production") throw new InvalidOperationException("Historical field updates require the verified FFPJM target.");
        if (update.Expected.Identity.PortalRequestId != update.Source.PortalRequestId)
            throw new InvalidOperationException("Destination identity does not match the source.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await PrepareFieldSessionAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await DemandFieldPermissionsAsync(connection, transaction, cancellationToken);
        // Serializable key-range/update locks prevent concurrent identity inserts and destination edits through commit.
        var current = await ReadFieldSnapshotAsync(connection, transaction, update.Source.PortalRequestId, cancellationToken);
        preview.ValidateCurrent(current);
        // Every identifier comes from the fixed mapping allowlist, never a client-provided SQL fragment.
        var assignments = update.ApprovedFields.Select(name => "[" + name + "]=@" + name);
        string sql = "DECLARE @updated TABLE (ID bigint); UPDATE dbo.ApplicationLetters SET " + string.Join(",", assignments) +
            " OUTPUT inserted.ID INTO @updated WHERE ID=@ID AND ExternalRequestId=@RequestId AND GCRecord IS NULL; SELECT COUNT_BIG(*) FROM @updated;";
        await using (var command = new SqlCommand(sql, connection, transaction))
        {
            BridgeQuickIntakeFields.AddParameters(command, update.Source);
            command.Parameters.Add("@ID", SqlDbType.BigInt).Value = long.Parse(current.Identity.ApplicationId, System.Globalization.CultureInfo.InvariantCulture);
            command.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = update.Source.PortalRequestId;
            if (!Equals(await command.ExecuteScalarAsync(cancellationToken), 1L))
                throw new InvalidOperationException("Destination identity changed; field update was rolled back.");
        }
        var readback = await ReadFieldSnapshotAsync(connection, transaction, update.Source.PortalRequestId, cancellationToken);
        preview.ValidateReadback(readback, update.ApprovedFields);
        await transaction.CommitAsync(cancellationToken);
        return readback;
    }

    private static async Task DemandFieldPermissionsAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        const string sql = """
            SELECT COUNT(*) FROM (VALUES(N'Income'),(N'PayFrequency'),(N'MaritalStatus'),(N'Dependents'),
                (N'Disabled'),(N'Programme'),(N'Circumstance')) fields(Name)
            WHERE COALESCE(HAS_PERMS_BY_NAME(N'dbo.ApplicationLetters',N'OBJECT',N'UPDATE',Name,N'COLUMN'),0)<>1;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        if (!Equals(await command.ExecuteScalarAsync(token), 0))
            throw new IntegrationTransferException(TransferFailures.For("FieldUpdate", "FIELD_ACCESS"));
    }

    private static async Task PrepareFieldSessionAsync(SqlConnection connection, CancellationToken token)
    {
        // Required by the existing filtered ExternalRequestId index. Connection-local only;
        // no database options, schema, permissions or other integration paths are changed.
        const string sql = "SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET ANSI_WARNINGS ON; SET CONCAT_NULL_YIELDS_NULL ON; SET ARITHABORT ON; SET NUMERIC_ROUNDABORT OFF;";
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    private async Task<ApplicationFieldSnapshot> ReadFieldSnapshotAsync(SqlConnection connection, SqlTransaction transaction,
        Guid requestId, CancellationToken token)
    {
        // These are the existing verified FFPJM column types; no schema or reference counter changes.
        const string sql = """
            SELECT TOP (2) ID,ExternalRequestId,ReferenceNo,GCRecord,DateCreated,
                Income,PayFrequency,MaritalStatus,Dependents,Disabled,Programme,Circumstance,DateUpdated,ApplicationStatus
            FROM dbo.ApplicationLetters WITH (UPDLOCK,HOLDLOCK)
            WHERE ExternalRequestId=@RequestId ORDER BY ID;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@RequestId", SqlDbType.UniqueIdentifier).Value = requestId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token) || !reader.IsDBNull(3) || reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2)))
            throw new InvalidOperationException("A unique active FFP Manager identity could not be verified.");
        var identity = new ExternalApplicationReceipt(reader.GetGuid(1), reader.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(2), reader.IsDBNull(4) ? DateTimeOffset.UnixEpoch : JamaicaBusinessTime.ToUtc(reader.GetDateTime(4)),
            "JMDB01", FfpSqlConnection.DatabaseFor(options.SqlTarget));
        var fields = new ApplicationMappedFields(reader.IsDBNull(5) ? null : reader.GetDecimal(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetBoolean(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11));
        var snapshot = new ApplicationFieldSnapshot(identity, fields, reader.IsDBNull(12) ? null : reader.GetDateTime(12), reader.IsDBNull(13) ? null : reader.GetInt64(13));
        if (await reader.ReadAsync(token)) throw new InvalidOperationException("Multiple destination identities match this request. Field updates are blocked.");
        return snapshot;
    }
}
