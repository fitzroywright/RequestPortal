using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Secrets;
using Microsoft.Extensions.Options;
using RequestPortal.Infrastructure.Messaging;

namespace RequestPortal.Web.Services;

public static class WhatsAppWebhookEndpoints
{
    public const int MaxBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapWhatsAppWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/webhooks/whatsapp", VerifyAsync).AllowAnonymous();
        endpoints.MapPost("/api/webhooks/whatsapp", ReceiveAsync).AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> VerifyAsync(HttpRequest request, IOptions<NotificationOptions> options,
        ISecretProvider secrets, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.WhatsAppWebhookEnabled) return Results.NotFound();
        if (request.QueryString.Value?.Length > 4096 ||
            !Parameter(request, "hub.mode", 16, out var mode) || mode != "subscribe" ||
            !Parameter(request, "hub.verify_token", 512, out var token) ||
            !Parameter(request, "hub.challenge", 1024, out var challenge)) return Results.StatusCode(403);
        try
        {
            string? expected = await secrets.GetAsync(settings.WhatsAppVerificationTokenSecretName, cancellationToken);
            if (string.IsNullOrWhiteSpace(expected)) return Results.StatusCode(503);
            // Hash both inputs to a fixed length before comparing, including unequal length tokens.
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                SHA256.HashData(Encoding.UTF8.GetBytes(token)))) return Results.StatusCode(403);
            return Results.Text(challenge, "text/plain", Encoding.UTF8);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return Results.StatusCode(503); }
    }

    private static bool Parameter(HttpRequest request, string name, int maxLength, out string value)
    {
        var values = request.Query[name];
        value = values.Count == 1 ? values[0] ?? "" : "";
        return value.Length > 0 && value.Length <= maxLength && !value.Any(char.IsControl);
    }

    private static async Task<IResult> ReceiveAsync(HttpRequest request, IOptions<NotificationOptions> options,
        ISecretProvider secrets, IWhatsAppWebhookStore store, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.WhatsAppWebhookEnabled) return Results.NotFound();
        if (request.ContentLength > MaxBodyBytes) return Results.StatusCode(413);
        var signatures = request.Headers["X-Hub-Signature-256"];
        string signature = signatures.Count == 1 ? signatures[0] ?? "" : "";
        if (signature.Length != 71 || !signature.StartsWith("sha256=", StringComparison.Ordinal))
            return Results.StatusCode(403);
        byte[] supplied;
        try { supplied = Convert.FromHexString(signature[7..]); }
        catch (FormatException) { return Results.StatusCode(403); }
        try
        {
            string? secret = await secrets.GetAsync(settings.WhatsAppAppSecretName, cancellationToken);
            if (string.IsNullOrWhiteSpace(secret)) return Results.StatusCode(503);
            using var body = new MemoryStream();
            byte[] buffer = new byte[16384];
            int count;
            while ((count = await request.Body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (body.Length + count > MaxBodyBytes) return Results.StatusCode(413);
                body.Write(buffer, 0, count);
            }
            byte[] raw = body.ToArray();
            if (!CryptographicOperations.FixedTimeEquals(supplied,
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), raw))) return Results.StatusCode(403);

            IReadOnlyList<WhatsAppWebhookEvent> events;
            try { events = Parse(raw, settings); }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return Results.BadRequest(); }
            await store.AcceptAsync(events, cancellationToken);
            return Results.Ok();
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413) { return Results.StatusCode(413); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // No exception text: provider/database exceptions can contain secrets or payloads.
            return Results.StatusCode(503);
        }
    }

    private static IReadOnlyList<WhatsAppWebhookEvent> Parse(byte[] raw, NotificationOptions settings)
    {
        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (Text(root, "object") != "whatsapp_business_account") throw new JsonException();
        var result = new List<WhatsAppWebhookEvent>();
        foreach (var entry in Array(root, "entry"))
        {
            string account = Required(entry, "id");
            if (account != settings.WhatsAppBusinessAccountId) throw new JsonException();
            foreach (var change in Array(entry, "changes"))
            {
                if (Text(change, "field") != "messages") continue;
                if (!change.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object) throw new JsonException();
                if (!value.TryGetProperty("metadata", out var metadata) ||
                    Text(metadata, "phone_number_id") != settings.WhatsAppPhoneNumberId) throw new JsonException();
                foreach (var message in OptionalArray(value, "messages"))
                {
                    string type = Required(message, "type");
                    result.Add(new(account, settings.WhatsAppPhoneNumberId, Required(message, "id"),
                        "inbound", type, Required(message, "timestamp"), Errors(message)));
                }
                foreach (var status in OptionalArray(value, "statuses"))
                {
                    string state = Required(status, "status");
                    result.Add(new(account, settings.WhatsAppPhoneNumberId, Required(status, "id"),
                        "status", state is "sent" or "delivered" or "read" or "failed" ? state : "unknown",
                        Required(status, "timestamp"), Errors(status)));
                }
                var errors = Errors(value);
                if (errors.Count > 0) result.Add(new(account, settings.WhatsAppPhoneNumberId,
                    Convert.ToHexString(SHA256.HashData(raw)), "error", "failed", "", errors));
            }
        }
        // Unknown events are durably accounted for without storing arbitrary applicant data.
        if (result.Count == 0) result.Add(new(settings.WhatsAppBusinessAccountId, settings.WhatsAppPhoneNumberId,
            Convert.ToHexString(SHA256.HashData(raw)), "ignored", "unknown", "", []));
        return result;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string Required(JsonElement element, string name)
    {
        string? value = Text(element, name);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)) throw new JsonException();
        return value;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new JsonException();
        return value.EnumerateArray();
    }

    private static IEnumerable<JsonElement> OptionalArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out _) ? Array(element, name) : [];

    private static IReadOnlyList<int> Errors(JsonElement element) => OptionalArray(element, "errors")
        .Select(error => error.TryGetProperty("code", out var code) && code.TryGetInt32(out int number) ? number : 0).ToArray();
}
