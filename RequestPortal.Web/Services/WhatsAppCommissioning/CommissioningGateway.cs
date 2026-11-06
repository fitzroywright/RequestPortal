using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Common.Messaging;
using Common.Messaging.Channels.WhatsApp;
using RequestPortal.Infrastructure.Messaging;

namespace RequestPortal.Web.Services.WhatsAppCommissioning;

public sealed record MetaTestResult(string State, int? HttpStatus = null, int? ErrorCode = null, int? ErrorSubcode = null, string? MessageId = null);
public sealed record WhatsAppTestReadiness(bool Ready, string Stage, string PhoneNumberId, string ApiVersion,
    string TokenReference, bool MessagingPermissionConfirmed = false, bool TemplateApproved = false,
    int? HttpStatus = null, int? ErrorCode = null, int? ErrorSubcode = null);

// Reuses Common.Messaging's serializer and secret resolver. Never registers a channel
// in the shared queue. Graph bodies and bearer headers never enter diagnostic logging.
public sealed class CommissioningGateway(HttpClient graph, IChannelSecretResolver secrets)
{
    public const string Phone = "1427917097069650", Account = "1823610105620506";
    public const string TokenReference = "messaging/whatsapp/api-token";
    public static void RequireTestConfiguration(NotificationOptions options)
    {
        if (options.WhatsAppEnabled || !options.WhatsAppWebhookEnabled ||
            options.WhatsAppPhoneNumberId != Phone || options.WhatsAppBusinessAccountId != Account ||
            options.WhatsAppAccessTokenSecretName != TokenReference ||
            !Regex.IsMatch(options.WhatsAppGraphApiVersion ?? "", @"^v[1-9][0-9]*\.[0-9]+$"))
            throw new InvalidOperationException("Commissioning requires the configured Meta test sender, webhook and secret reference, with general outbound disabled.");
    }

    public async Task<WhatsAppTestReadiness> InspectAsync(NotificationOptions options, CancellationToken ct)
    {
        RequireTestConfiguration(options);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        ct = deadline.Token;
        string stage = "token";
        try
        {
            string? token = await secrets.GetSecretAsync(TokenReference, ct);
            if (string.IsNullOrWhiteSpace(token)) return Result(false);
            stage = "sender";
            var phone = await GetAsync($"{Phone}?fields=id,code_verification_status,platform_type", token, ct);
            if (phone.Error is not null) return Result(false, phone.Error);
            if (phone.Json?.GetProperty("id").GetString() != Phone ||
                phone.Json?.GetProperty("code_verification_status").GetString() != "VERIFIED" ||
                phone.Json?.GetProperty("platform_type").GetString() != "CLOUD_API") return Result(false);
            stage = "permissions";
            var permissions = await GetAsync("me/permissions", token, ct);
            if (permissions.Error is not null) return Result(false, permissions.Error);
            bool allowed = permissions.Json?.GetProperty("data").EnumerateArray().Any(x =>
                x.GetProperty("permission").GetString() == "whatsapp_business_messaging" && x.GetProperty("status").GetString() == "granted") == true;
            if (!allowed) return Result(false);
            stage = "template";
            var templates = await GetAsync($"{Account}/message_templates?name=hello_world&fields=name,status,language&limit=100", token, ct);
            if (templates.Error is not null) return Result(false, templates.Error, allowed);
            bool approved = templates.Json?.GetProperty("data").EnumerateArray().Any(x =>
                x.GetProperty("name").GetString() == "hello_world" && x.GetProperty("language").GetString() == "en_US" &&
                x.GetProperty("status").GetString() == "APPROVED") == true;
            return Result(approved, permission: allowed, approved: approved);
        }
        catch (Exception) { return Result(false); }
        WhatsAppTestReadiness Result(bool ready, MetaTestResult? error = null, bool permission = false, bool approved = false) =>
            new(ready, ready ? "ready" : stage, Phone, options.WhatsAppGraphApiVersion, TokenReference,
                permission, approved, error?.HttpStatus, error?.ErrorCode, error?.ErrorSubcode);
        async Task<(JsonElement? Json, MetaTestResult? Error)> GetAsync(string path, string token, CancellationToken cancellation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://graph.facebook.com/{options.WhatsAppGraphApiVersion}/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await graph.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            var json = await ReadBoundedJsonAsync(response.Content, cancellation);
            return response.IsSuccessStatusCode ? (json, null) : (null, Error(response.StatusCode, json));
        }
    }

    public async Task<MetaTestResult> SendHelloWorldAsync(NotificationOptions options, string recipient, Guid operationId, CancellationToken ct)
    {
        RequireTestConfiguration(options);
        if (!Regex.IsMatch(recipient ?? "", @"^\+[1-9][0-9]{6,14}$")) throw new InvalidOperationException("A verified E.164 test recipient is required.");
        using var capture = new CaptureHandler(graph);
        using var client = new HttpClient(capture) { Timeout = TimeSpan.FromSeconds(25) };
        var channel = new WhatsAppMessageChannel(client, new WhatsAppMessageOptions {
            Enabled = true, GraphApiVersion = options.WhatsAppGraphApiVersion, PhoneNumberId = Phone,
            AccessTokenSecretName = TokenReference, AccessToken = ""
        }, secrets);
        try
        {
            await channel.SendAsync(new MessageRecipient("commissioning", "Authorized test recipient", Mobile: recipient!.TrimStart('+'), PreferredChannels: MessageChannel.WhatsApp),
                new MessageRequest { Channels = MessageChannel.WhatsApp, BodyIsHtml = false, Source = "RequestPortal.WhatsAppCommissioning",
                    CorrelationId = operationId.ToString("D"), Metadata = new Dictionary<string,string> {
                        ["WhatsAppTemplateName"] = "hello_world", ["WhatsAppTemplateLanguage"] = "en_US"
                    } }, operationId, ct);
        }
        catch (Exception) { /* No raw provider, HTTP or payload exception text. Never retry here. */ }
        return capture.Result ?? new(capture.Started ? "Unknown" : "NotSent");
    }

    internal static async Task<JsonElement> ReadBoundedJsonAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] block = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + count > 65536) throw new InvalidOperationException("Meta response exceeds the safe limit.");
            buffer.Write(block, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
    private static MetaTestResult Error(HttpStatusCode status, JsonElement? json)
    {
        int? code = null, subcode = null;
        if (json is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("code", out var c) && c.TryGetInt32(out int value)) code = value;
            if (error.TryGetProperty("error_subcode", out var s) && s.TryGetInt32(out int sub)) subcode = sub;
        }
        return new((int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout ? "Rejected" : "Unknown", (int)status, code, subcode);
    }
    private sealed class CaptureHandler(HttpClient graph) : HttpMessageHandler
    {
        public bool Started { get; private set; }
        public MetaTestResult? Result { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // The inner factory client has no loggers, redirects or retry handlers.
            var payload = JsonNode.Parse(await request.Content!.ReadAsByteArrayAsync(ct)) ?? throw new InvalidOperationException();
            // Common.Messaging emits components:null for templates without parameters;
            // omit that optional member for the approved parameterless hello_world request.
            if (payload["template"] is JsonObject template && template["components"] is null) template.Remove("components");
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri) {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
            forwarded.Headers.Authorization = request.Headers.Authorization;
            Started = true;
            var response = await graph.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, ct);
            try
            {
                var json = await ReadBoundedJsonAsync(response.Content, ct);
                if (!response.IsSuccessStatusCode) Result = Error(response.StatusCode, json);
                else if (json.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array && messages.GetArrayLength() == 1 &&
                    messages[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
                    id.GetString() is { Length: > 6 and <= 512 } messageId && messageId.StartsWith("wamid.", StringComparison.Ordinal) && !messageId.Any(char.IsControl))
                    Result = new("Accepted", (int)response.StatusCode, MessageId: messageId);
                else Result = new("Unknown", (int)response.StatusCode);
            }
            catch (Exception) { Result = new("Unknown", (int)response.StatusCode); }
            finally
            {
                response.Content.Dispose();
                // The shared channel throws its response body on failure: replace it first.
                response.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                response.ReasonPhrase = "Meta response";
            }
            return response;
        }
    }
}
