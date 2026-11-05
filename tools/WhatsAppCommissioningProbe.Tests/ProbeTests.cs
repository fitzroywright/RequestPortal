using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Xunit;
using Probe = RequestPortal.Tools.WhatsAppCommissioning.Program;

namespace WhatsAppCommissioningProbe.Tests;

public sealed class ProbeTests
{
    [Fact]
    public async Task ProbeVerifiesSignaturesAndPersistedDeduplicationWithoutTouchingApplicantTables()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync(ct);
        using var handler = new Endpoint(database.ConnectionString);
        using var http = new HttpClient(handler);
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await Probe.VerifyAsync("fixture-verification", "fixture-app-secret", connection, ct, http);
        Assert.Equal(7, handler.Requests);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM outbox_messages", connection);
        Assert.Equal(2L, await count.ExecuteScalarAsync(ct));
    }

    [Theory]
    [InlineData("signature")][InlineData("durability")][InlineData("duplicate")][InlineData("challenge")]
    public async Task ProbeFailsOnInsecureAcceptanceLostReceiptsDuplicatesOrWrongChallenge(string fault)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await Database.CreateAsync(ct);
        using var http = new HttpClient(new Endpoint(database.ConnectionString, fault));
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Probe.VerifyAsync("fixture-verification", "fixture-app-secret", connection, ct, http));
    }

    [Theory]
    [InlineData("Logging:Console:LogLevel:Default", "Information", false)]
    [InlineData("Logging:Console:LogLevel:Microsoft.AspNetCore", "Trace", false)]
    [InlineData("Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics", "Debug", false)]
    [InlineData("Logging:Console:LogLevel:Microsoft", "Warning", true)]
    public void UnsafeProviderOverridesCannotLeakVerificationQueries(string key, string level, bool safe)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning", [key] = level }).Build();
        Assert.Equal(safe, Probe.RequestLoggingIsSafe(config));
    }

    private sealed class Endpoint(string connectionString, string? fault = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        private int validPosts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.Method == HttpMethod.Get)
            {
                var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
                if (query["hub.verify_token"] != "fixture-verification") return new(HttpStatusCode.Forbidden);
                return new(HttpStatusCode.OK) { Content = new StringContent(fault == "challenge" ? "wrong" : query["hub.challenge"].ToString(), Encoding.UTF8, "text/plain") };
            }
            byte[] bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            string expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("fixture-app-secret"), bytes)).ToLowerInvariant();
            if (!request.Headers.TryGetValues("X-Hub-Signature-256", out var values) || values.Single() != expected)
                return new(fault == "signature" ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
            validPosts++;
            using var document = JsonDocument.Parse(bytes);
            var value = document.RootElement.GetProperty("entry")[0].GetProperty("changes")[0].GetProperty("value");
            var message = value.GetProperty("messages")[0];
            string id = message.GetProperty("id").GetString()!, timestamp = message.GetProperty("timestamp").GetString()!;
            if (fault != "durability")
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(ct);
                foreach (Guid receipt in new[] { Probe.ReceiptId(id, "inbound", "", ""), Probe.ReceiptId(id, "status", "delivered", timestamp) })
                {
                    Guid actual = fault == "duplicate" && validPosts > 1 ? Guid.NewGuid() : receipt;
                    await using var command = new NpgsqlCommand("""
                        INSERT INTO inbox_messages VALUES (@id,'RequestPortal.WhatsAppWebhook.v1',now()) ON CONFLICT DO NOTHING;
                        INSERT INTO outbox_messages VALUES (@id,'RequestPortal.WhatsAppWebhook.v1',now(), CAST(@payload AS jsonb)) ON CONFLICT DO NOTHING;
                        """, connection);
                    command.Parameters.AddWithValue("id", actual); command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(new { MessageId = id }));
                    await command.ExecuteNonQueryAsync(ct);

                }
            }
            return new(HttpStatusCode.OK);
        }
    }

    private sealed class Database(string baseConnection, string schema, string connectionString) : IAsyncDisposable
    {
        public string ConnectionString => connectionString;
        public static async Task<Database> CreateAsync(CancellationToken ct)
        {
            string baseConnection = Environment.GetEnvironmentVariable("REQUESTPORTAL_POSTGRES") ?? throw new InvalidOperationException("Isolated REQUESTPORTAL_POSTGRES is required.");
            string schema = "rp_probe_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(baseConnection);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand($"""
                CREATE SCHEMA "{schema}";
                CREATE TABLE "{schema}".inbox_messages ("MessageId" uuid PRIMARY KEY,"Consumer" text,"ProcessedUtc" timestamptz);
                CREATE TABLE "{schema}".outbox_messages ("Id" uuid PRIMARY KEY,"MessageType" text,"ProcessedUtc" timestamptz,"Payload" jsonb);
                """, connection);
            await command.ExecuteNonQueryAsync(ct);
            return new(baseConnection, schema, new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema, Pooling = false }.ConnectionString);
        }
        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(baseConnection); await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", connection); await command.ExecuteNonQueryAsync();
        }
    }
}
