using FfpManager.Bridge;
using Microsoft.Data.SqlClient;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class FfpManagerReferenceNumberTests
{
    private readonly ITestOutputHelper output;

    public FfpManagerReferenceNumberTests(ITestOutputHelper output) => this.output = output;

    [Theory]
    [InlineData("KIN", 1, "KIN/26/GR/0001")]
    [InlineData("STC", 12345, "STC/26/GR/12345")]
    public void Format_UsesFfpParishYearAndSharedSequence(string parish, long number, string expected) =>
        Assert.Equal(expected, FfpManagerReferenceNumberGenerator.Format(parish, new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), number));

    [Trait("Category", "LiveFfpSql")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gateway_OnFfpTestDatabase_ReturnsSavedReference_AndIsIdempotent(bool synced)
    {
        string? target = Environment.GetEnvironmentVariable("FFPMANAGER_TEST");
        if (string.IsNullOrWhiteSpace(target))
            Assert.Skip("FFPMANAGER_TEST must point to the FFP acceptance database.");
        FfpSqlConnection.ValidateTarget(target!, "Rehearsal");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqlConnection connection = await FfpSqlConnection.OpenAsync(target!, "Rehearsal", token);
        string parish;
        await using (SqlCommand lookup = new("SELECT TOP (1) Name FROM dbo.Parishes WHERE ID > 0 AND NULLIF(LTRIM(RTRIM(Name)),'') IS NOT NULL AND NULLIF(LTRIM(RTRIM(Code)),'') IS NOT NULL ORDER BY ID", connection))
            parish = (string?)await lookup.ExecuteScalarAsync(token)
                ?? throw new InvalidOperationException("The test database requires an active parish.");
        Guid requestId = Guid.NewGuid();
        var submission = new RequestPortal.Application.Integration.ExternalApplicationSubmission(
            Guid.NewGuid(), requestId, "RP-ACCEPT-" + requestId.ToString("N"), Guid.NewGuid(),
            "Request Portal reference acceptance test", null, "Test address", "+18765550123", "test@example.invalid",
            Guid.NewGuid(), DateTimeOffset.UtcNow, "GoodsAndServices", "Normal", "Reference acceptance test",
            "Food", null, null, parish, "Automated acceptance on FFP test database only.",
            [new(Guid.NewGuid(), "Synthetic acceptance item", 2, "EACH")], [], parish,
            "Request Portal - Quick Intake");
        var options = Microsoft.Extensions.Options.Options.Create(new BridgeOptions
        {
            SqlConnectionString = target!, SqlTarget = "Rehearsal", InitialApplicationStatus = "AWAITING APPROVAL",
            StatusMap = new(StringComparer.OrdinalIgnoreCase) { ["AWAITING APPROVAL"] = "RECEIVED" }
        });
        IFfpManagerGateway gateway = synced
            ? new SyncedSqlFfpManagerGateway(options, new FfpManagerReferenceDataCatalog(options))
            : new SqlFfpManagerGateway(options);
        try
        {
            var first = await gateway.CreateApplicationAsync(submission, token);
            var repeated = await gateway.CreateApplicationAsync(submission, token);
            Assert.Equal(first.ApplicationId, repeated.ApplicationId);
            Assert.Equal(first.ApplicationNumber, repeated.ApplicationNumber);
            Assert.NotEqual(submission.PublicReference, first.ApplicationNumber);
            Assert.True(repeated.AlreadyExisted);
            await using SqlCommand stored = new(
                "SELECT Code,ReferenceNo,DocumentSource,Parish FROM dbo.ApplicationLetters WHERE ExternalRequestId=@Id AND GCRecord IS NULL",
                connection);
            stored.Parameters.AddWithValue("@Id", requestId);
            await using SqlDataReader reader = await stored.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token));
            Assert.Equal(submission.PublicReference, reader.GetString(0));
            Assert.Equal(first.ApplicationNumber, reader.GetString(1));
            Assert.Equal(submission.DocumentSource, reader.GetString(2));
            Assert.False(reader.IsDBNull(3));
            Assert.False(await reader.ReadAsync(token));
            await reader.CloseAsync();
            await using SqlCommand items = new(
                "SELECT COUNT(*) FROM dbo.ItemsRequested ir JOIN dbo.ApplicationLetters al ON al.ID=ir.ApplicationLetterID JOIN dbo.UnitsOfMeasure u ON u.ID=ir.UnitOfMeasure WHERE al.ExternalRequestId=@Id AND ir.Quantity=2 AND u.Name=N'EACH'",
                connection);
            items.Parameters.AddWithValue("@Id", requestId);
            Assert.Equal(1, Convert.ToInt32(await items.ExecuteScalarAsync(token)));
            Assert.Equal("RECEIVED", (await gateway.GetStatusAsync(first.ApplicationId, token))!.StatusCode);
            output.WriteLine($"Verified gateway={(synced ? "SyncedSql" : "Sql")}; RequestId={requestId}; ApplicationId={first.ApplicationId}; Code={submission.PublicReference}; ReferenceNo={first.ApplicationNumber}; Parish={parish}; DocumentSource={submission.DocumentSource}; Items=1; Quantity=2; Unit=EACH; duplicate delivery reused identity/reference.");
            Guid statusEventId = Guid.NewGuid();
            Guid informationEventId = Guid.NewGuid();
            await using (SqlTransaction eventTransaction = (SqlTransaction)await connection.BeginTransactionAsync(token))
            {
                await using SqlCommand enqueue = new("""
                    UPDATE dbo.ApplicationLetters
                    SET ApplicationStatus=(SELECT TOP (1) ID FROM dbo.ApplicationStatusTypes WHERE Name=N'COMPLETE' ORDER BY ID)
                    WHERE ID=@ApplicationId AND ExternalRequestId=@RequestId;
                    EXEC dbo.RequestPortal_EnqueueApplicationStatusChanged @ApplicationLetterId=@ApplicationId,@EventId=@StatusEventId;
                    EXEC dbo.RequestPortal_EnqueueApplicationStatusChanged @ApplicationLetterId=@ApplicationId,@EventId=@StatusEventId;
                    EXEC dbo.RequestPortal_EnqueueInformationRequest @ApplicationLetterId=@ApplicationId,@Message=N'Synthetic rollback acceptance',@EventId=@InformationEventId;
                    EXEC dbo.RequestPortal_EnqueueInformationRequest @ApplicationLetterId=@ApplicationId,@Message=N'Synthetic rollback acceptance',@EventId=@InformationEventId;
                    SELECT COUNT(*) FROM dbo.RequestPortalIntegrationOutbox WHERE PortalRequestId=@RequestId AND EventId IN (@StatusEventId,@InformationEventId);
                    SELECT COUNT(*) FROM dbo.ApplicationNotes WHERE ApplicationLetterID=@ApplicationId AND Code=LEFT(REPLACE(CONVERT(nvarchar(36),@InformationEventId),N'-',N''),25);
                    """, connection, eventTransaction);
                enqueue.Parameters.AddWithValue("@ApplicationId", long.Parse(first.ApplicationId));
                enqueue.Parameters.AddWithValue("@RequestId", requestId);
                enqueue.Parameters.AddWithValue("@StatusEventId", statusEventId);
                enqueue.Parameters.AddWithValue("@InformationEventId", informationEventId);
                await using (SqlDataReader events = await enqueue.ExecuteReaderAsync(token))
                {
                    Assert.True(await events.ReadAsync(token));
                    Assert.Equal(2, events.GetInt32(0));
                    Assert.True(await events.NextResultAsync(token));
                    Assert.True(await events.ReadAsync(token));
                    Assert.Equal(1, events.GetInt32(0));
                }
                await eventTransaction.RollbackAsync(CancellationToken.None);
            }
            await using SqlCommand rolledBack = new("SELECT COUNT(*) FROM dbo.RequestPortalIntegrationOutbox WHERE PortalRequestId=@RequestId", connection);
            rolledBack.Parameters.AddWithValue("@RequestId", requestId);
            Assert.Equal(0, Convert.ToInt32(await rolledBack.ExecuteScalarAsync(token)));
            Assert.Equal("RECEIVED", (await gateway.GetStatusAsync(first.ApplicationId, token))!.StatusCode);
            output.WriteLine("Verified both enqueue procedures deduplicate EventId within the business transaction; rollback removed events and restored status. No callback event was committed.");
        }
        finally
        {
            // No production access and no counter rewinds: sequence gaps are valid.
            await using SqlCommand cleanup = new("SET XACT_ABORT ON; BEGIN TRANSACTION; DELETE ir FROM dbo.ItemsRequested ir JOIN dbo.ApplicationLetters al ON al.ID=ir.ApplicationLetterID WHERE al.ExternalRequestId=@Id; DELETE FROM dbo.ApplicationLetters WHERE ExternalRequestId=@Id; COMMIT;", connection);
            cleanup.Parameters.AddWithValue("@Id", requestId);
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Trait("Category", "LiveFfpSql")]
    [Fact]
    public async Task SharedCounter_OnFfpTestDatabase_AllocatesDistinctNumbers_AndRollsBack()
    {
        string? target = Environment.GetEnvironmentVariable("FFPMANAGER_TEST");
        if (string.IsNullOrWhiteSpace(target))
            Assert.Skip("FFPMANAGER_TEST must point to the FFP acceptance database.");
        FfpSqlConnection.ValidateTarget(target!, "Rehearsal");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqlConnection connection = await FfpSqlConnection.OpenAsync(target!, "Rehearsal", token);
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
        try
        {
            string parish;
            await using (SqlCommand lookup = new("SELECT TOP (1) Name FROM dbo.Parishes WHERE ID > 0 AND NULLIF(LTRIM(RTRIM(Name)),'') IS NOT NULL AND NULLIF(LTRIM(RTRIM(Code)),'') IS NOT NULL ORDER BY ID", connection, transaction))
                parish = (string?)await lookup.ExecuteScalarAsync(token)
                    ?? throw new InvalidOperationException("The test database requires an active parish.");
            var first = await FfpManagerReferenceNumberGenerator.GenerateAsync(connection, transaction, parish, DateTimeOffset.UtcNow, token);
            var second = await FfpManagerReferenceNumberGenerator.GenerateAsync(connection, transaction, parish, DateTimeOffset.UtcNow, token);
            Assert.Equal(first.ParishId, second.ParishId);
            Assert.NotEqual(first.ReferenceNo, second.ReferenceNo);
            Assert.Contains("/GR/", first.ReferenceNo);
        }
        finally
        {
            // Real test database operations; no committed changes to its shared sequence.
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }
}
