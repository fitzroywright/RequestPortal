using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Secrets;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace RequestPortal.Tools.WhatsAppCommissioning;

// Standalone offline operator probe. Never starts RequestPortal, runs migrations,
// sends messages, changes accounts, or writes secrets. All provider output stays private.
internal static class Program
{
    private const string Account = "1823610105620506", Phone = "1427917097069650";
    private const string Consumer = "RequestPortal.WhatsAppWebhook.v1";
    private const string Callback = "https://requestportal.ffpja.org/api/webhooks/whatsapp";
    private static string phase = "arguments";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || args[0] is not ("preflight" or "verify" or "scan-logs")) throw new InvalidOperationException();
            if (!long.TryParse(args[1], out long since) || since < 1) throw new InvalidOperationException();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var ct = deadline.Token;
            phase = "configuration";
            var initial = new ConfigurationBuilder().SetBasePath("/opt/aegis/requestportal")
                .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Production.json").AddEnvironmentVariables().Build();
            var overrides = new Dictionary<string, string?> { ["CommonSecrets:OpenBao:DeleteSecretIdFileAfterRead"] = "false" };
            foreach (var definition in initial.GetSection("CommonSecrets:Providers").GetChildren())
                overrides[$"CommonSecrets:Providers:{definition.Key}:Settings:DeleteSecretIdFileAfterRead"] = "false";
            var config = new ConfigurationBuilder().AddConfiguration(initial).AddInMemoryCollection(overrides).Build();
            if (config.GetValue("Notifications:WhatsAppEnabled", false) || config["ASPNETCORE_ENVIRONMENT"] != "Production" ||
                !config.GetValue("Notifications:WhatsAppWebhookEnabled", false) ||
                config["Notifications:WhatsAppPhoneNumberId"] != Phone || config["Notifications:WhatsAppBusinessAccountId"] != Account ||
                config["Notifications:WhatsAppAccessTokenSecretName"] != "messaging/whatsapp/api-token" ||
                config["Notifications:WhatsAppVerificationTokenSecretName"] != "messaging/whatsapp/verification-token" ||
                config["Notifications:WhatsAppAppSecretName"] != "messaging/whatsapp/app-secret") throw new InvalidOperationException();
            var order = config.GetSection("CommonSecrets:ProviderOrder").Get<string[]>();
            if (order is not ["OpenBao"] || config["CommonSecrets:Mode"] != "Production") throw new InvalidOperationException();
            // Fail closed if request logging could disclose a verification query string.
            if (!RequestLoggingIsSafe(config)) throw new InvalidOperationException();
            phase = "approle-secret-read";
            await using var runtime = CommonSecretsBootstrapRuntime.Create(config);
            // Use the actual OpenBao provider, never a development fallback provider.
            if (runtime.ProviderHealth.SingleOrDefault()?.ProviderName != "OpenBao" ||
                config["CommonSecrets:Providers:OpenBao:Type"] != "OpenBao" ||
                !config.GetValue("CommonSecrets:Providers:OpenBao:Settings:RequireHttps", false)) throw new InvalidOperationException();
            var provider = runtime.Provider;
            phase = "approle-verification-token-read";
            string verification = await provider.GetRequiredAsync("messaging/whatsapp/verification-token", ct);
            phase = "approle-app-secret-read";
            string appSecret = await provider.GetRequiredAsync("messaging/whatsapp/app-secret", ct);
            phase = "approle-api-token-read";
            string apiToken = await provider.GetRequiredAsync("messaging/whatsapp/api-token", ct);
            if (verification.Length > 512 || verification.Any(char.IsControl)) throw new InvalidOperationException();
            phase = "database-read";
            string connectionString = await provider.GetRequiredAsync("ConnectionStrings:RequestPortal", ct);
            var settings = new NpgsqlConnectionStringBuilder(connectionString) { IncludeErrorDetail = false };
            if (string.IsNullOrWhiteSpace(settings.Database) || settings.Database.Equals("FFPJM", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync(ct);
            await using (var transaction = await connection.BeginTransactionAsync(ct))
            {
                await using var command = new NpgsqlCommand("SET TRANSACTION READ ONLY; SELECT \"MessageId\", \"Consumer\", \"ProcessedUtc\" FROM inbox_messages LIMIT 0; SELECT \"Id\", \"MessageType\", \"Payload\", \"ProcessedUtc\" FROM outbox_messages LIMIT 0", connection, transaction);
                await command.ExecuteNonQueryAsync(ct);
                await transaction.RollbackAsync(ct);
            }
            if (args[0] == "verify")
            {
                phase = "https-verification";
                await VerifyAsync(verification, appSecret, connection, ct);
            }
            if (args[0] == "scan-logs")
            {
                phase = "log-privacy";
                ScanLogs([verification, appSecret, apiToken], since);
            }
            Console.WriteLine(JsonSerializer.Serialize(new { status = "ok", mode = args[0], secretsReadable = true,
                secretReads = new[] { "messaging/whatsapp/verification-token", "messaging/whatsapp/app-secret", "messaging/whatsapp/api-token" },
                outboundEnabled = false, credentialValidity = "not-tested-no-outbound-send", databaseReadOnly = true }));
            return 0;
        }
        catch
        {
            // No exception messages, URIs, response bodies, connection strings or values.
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", phase }));
            return 1;
        }
    }

    internal static bool RequestLoggingIsSafe(IConfiguration config)
    {
        static bool Safe(string? value) => value is "Warning" or "Error" or "Critical" or "None";
        if (!Safe(config["Logging:LogLevel:Microsoft.AspNetCore"])) return false;
        if (config.GetSection("Logging:LogLevel").GetChildren().Any(x =>
            x.Key.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase) && !Safe(x.Value))) return false;
        foreach (var provider in config.GetSection("Logging").GetChildren())
            if (provider.GetSection("LogLevel").GetChildren().Any(x =>
                (x.Key is "Default" or "Microsoft" || x.Key.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase)) && !Safe(x.Value))) return false;
        return true;
    }

    internal static async Task VerifyAsync(string verification, string secret, NpgsqlConnection connection, CancellationToken ct, HttpClient? client = null)
    {
        using var ownedClient = client is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) } : null;
        var http = client ?? ownedClient!;
        string challenge = "rp-commission-" + Guid.NewGuid().ToString("N");
        using var valid = await http.GetAsync(Callback + "?hub.mode=subscribe&hub.verify_token=" + Uri.EscapeDataString(verification) + "&hub.challenge=" + challenge, ct);
        if (valid.StatusCode != HttpStatusCode.OK || await valid.Content.ReadAsStringAsync(ct) != challenge || valid.Content.Headers.ContentType?.MediaType != "text/plain") throw new InvalidOperationException();
        using var invalid = await http.GetAsync(Callback + "?hub.mode=subscribe&hub.verify_token=invalid-" + challenge + "&hub.challenge=" + challenge, ct);
        if (invalid.StatusCode != HttpStatusCode.Forbidden) throw new InvalidOperationException();
        string id = "wamid.rp-commission." + Guid.NewGuid().ToString("N");
        string timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { @object = "whatsapp_business_account", entry = new[] { new { id = Account, changes = new[] { new { field = "messages", value = new {
            metadata = new { phone_number_id = Phone },
            messages = new[] { new { id, type = "text", timestamp, text = new { body = "Synthetic operator commissioning receipt" } } },
            statuses = new[] { new { id, status = "delivered", timestamp } }
        } } } } } });
        Guid inbound = ReceiptId(id, "inbound", "", ""), delivered = ReceiptId(id, "status", "delivered", timestamp);
        await AssertReceiptsAsync(connection, id, inbound, delivered, 0, ct);
        foreach (string variant in new[] { "unsigned", "invalid", "tampered", "valid", "duplicate" })
        {
            byte[] bytes = variant == "tampered" ? [..body, (byte)' '] : body;
            using var request = new HttpRequestMessage(HttpMethod.Post, Callback) { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (variant != "unsigned") request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(variant == "invalid" ? "invalid-test-secret" : secret), body)).ToLowerInvariant());
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode != (variant is "valid" or "duplicate" ? HttpStatusCode.OK : HttpStatusCode.Forbidden)) throw new InvalidOperationException();
            await AssertReceiptsAsync(connection, id, inbound, delivered, variant is "valid" or "duplicate" ? 2 : 0, ct);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { syntheticMessageId = id, receiptIds = new[] { inbound, delivered },
            validGet = 200, invalidGet = 403, unsignedPost = 403, invalidSignature = 403, tamperedPost = 403, signedPost = 200, duplicatePost = 200, durableReceipts = 2 }));
    }

    internal static Guid ReceiptId(string id, string kind, string state, string timestamp) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { Account, Phone, id, kind, state, timestamp })))[..16]);

    private static async Task AssertReceiptsAsync(NpgsqlConnection connection, string messageId, Guid first, Guid second, int expected, CancellationToken ct)
    {
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction)) await readOnly.ExecuteNonQueryAsync(ct);
        foreach (bool inbox in new[] { true, false })
        {
            await using var command = new NpgsqlCommand(inbox
                ? "SELECT count(*) FROM inbox_messages WHERE \"MessageId\" IN (@a,@b) AND \"Consumer\"=@consumer AND \"ProcessedUtc\" IS NOT NULL"
                : "SELECT count(*) FROM outbox_messages WHERE \"Id\" IN (@a,@b) AND \"MessageType\"=@consumer AND \"ProcessedUtc\" IS NOT NULL", connection, transaction);
            command.Parameters.AddWithValue("a", first); command.Parameters.AddWithValue("b", second); command.Parameters.AddWithValue("consumer", Consumer);
            if ((long)(await command.ExecuteScalarAsync(ct))! != expected) throw new InvalidOperationException();
        }
        await using (var matching = new NpgsqlCommand("SELECT count(*) FROM outbox_messages WHERE \"MessageType\"=@consumer AND \"Payload\"->>'MessageId'=@message", connection, transaction))
        {
            matching.Parameters.AddWithValue("consumer", Consumer); matching.Parameters.AddWithValue("message", messageId);
            if ((long)(await matching.ExecuteScalarAsync(ct))! != expected) throw new InvalidOperationException();
        }
        await transaction.RollbackAsync(ct);
    }

    private static void ScanLogs(string[] secrets, long since)
    {
        static bool ContainsSecret(string text, IEnumerable<string> values) => values.Any(value =>
            text.Contains(value, StringComparison.Ordinal) || text.Contains(Uri.EscapeDataString(value), StringComparison.OrdinalIgnoreCase));
        foreach (string path in new[] { "/var/log/nginx/access.log", "/var/log/nginx/access.log.1", "/var/log/nginx/error.log", "/var/log/nginx/error.log.1" })
        {
            if (!File.Exists(path)) { if (path.EndsWith(".1", StringComparison.Ordinal)) continue; throw new InvalidOperationException(); }
            // Scan line by line, including the previous rotation. Never print a matching line.
            foreach (string line in File.ReadLines(path))
                if (ContainsSecret(line, secrets)) throw new InvalidOperationException();
        }
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/journalctl") { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, ArgumentList = { "--quiet", "-u", "aegis-requestportal", "--since", "@" + since, "--no-pager", "-o", "cat" } }) ?? throw new InvalidOperationException();
        string stdout = process.StandardOutput.ReadToEnd(); string stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0 || stderr.Length > 0 || ContainsSecret(stdout, secrets)) throw new InvalidOperationException();
    }
}
