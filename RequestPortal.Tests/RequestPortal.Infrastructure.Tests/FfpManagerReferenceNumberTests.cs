using FfpManager.Bridge;
using Microsoft.Data.SqlClient;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class FfpManagerReferenceNumberTests
{
    [Theory]
    [InlineData("KIN", 1, "KIN/26/GR/0001")]
    [InlineData("STC", 12345, "STC/26/GR/12345")]
    public void Format_UsesFfpParishYearAndSharedSequence(string parish, long number, string expected) =>
        Assert.Equal(expected, FfpManagerReferenceNumberGenerator.Format(parish, new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), number));

    [Fact]
    public async Task Gateway_OnFfpTestDatabase_ReturnsSavedReference_AndIsIdempotent()
    {
        string? target = Environment.GetEnvironmentVariable("FFPMANAGER_TEST");
        if (string.IsNullOrWhiteSpace(target))
            Assert.Skip("FFPMANAGER_TEST must point to the FFP acceptance database.");
        SqlConnectionStringBuilder settings = new(target!);
        Assert.Equal("FFP", settings.InitialCatalog, ignoreCase: true);
        Assert.Equal("JMDB01", settings.DataSource, ignoreCase: true);
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqlConnection connection = new(target);
        await connection.OpenAsync(token);
        string parish;
        await using (SqlCommand lookup = new("SELECT TOP (1) Name FROM dbo.Parishes WHERE GCRecord IS NULL AND Code IS NOT NULL ORDER BY ID", connection))
            parish = (string?)await lookup.ExecuteScalarAsync(token)
                ?? throw new InvalidOperationException("The test database requires an active parish.");
        Guid requestId = Guid.NewGuid();
        var submission = new RequestPortal.Application.Integration.ExternalApplicationSubmission(
            Guid.NewGuid(), requestId, "RP-ACCEPT-" + requestId.ToString("N"), Guid.NewGuid(),
            "Request Portal reference acceptance test", null, "Test address", "+18765550123", "test@example.invalid",
            Guid.NewGuid(), DateTimeOffset.UtcNow, "GoodsAndServices", "Normal", "Reference acceptance test",
            "Food", null, null, parish, "Automated acceptance on FFP test database only.", [], [], parish,
            "Request Portal - Quick Intake");
        SqlFfpManagerGateway gateway = new(Microsoft.Extensions.Options.Options.Create(new BridgeOptions
        {
            SqlConnectionString = target!, InitialApplicationStatus = ""
        }));
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
        }
        finally
        {
            // No production access and no counter rewinds: sequence gaps are valid.
            await using SqlCommand cleanup = new("DELETE FROM dbo.ApplicationLetters WHERE ExternalRequestId=@Id", connection);
            cleanup.Parameters.AddWithValue("@Id", requestId);
            await cleanup.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SharedCounter_OnFfpTestDatabase_AllocatesDistinctNumbers_AndRollsBack()
    {
        string? target = Environment.GetEnvironmentVariable("FFPMANAGER_TEST");
        if (string.IsNullOrWhiteSpace(target))
            Assert.Skip("FFPMANAGER_TEST must point to the FFP acceptance database.");
        SqlConnectionStringBuilder settings = new(target!);
        Assert.Equal("FFP", settings.InitialCatalog, ignoreCase: true);
        Assert.Equal("JMDB01", settings.DataSource, ignoreCase: true);
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqlConnection connection = new(target);
        await connection.OpenAsync(token);
        await using SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
        try
        {
            string parish;
            await using (SqlCommand lookup = new("SELECT TOP (1) Name FROM dbo.Parishes WHERE GCRecord IS NULL AND Code IS NOT NULL ORDER BY ID", connection, transaction))
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
