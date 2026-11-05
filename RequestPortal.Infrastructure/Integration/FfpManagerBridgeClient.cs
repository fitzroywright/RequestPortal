using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Secrets;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;

namespace RequestPortal.Infrastructure.Integration;

public sealed class FfpManagerBridgeClient : IExternalApplicationBridgeClient
{
    private readonly HttpClient httpClient;
    private readonly FfpManagerBridgeOptions options;
    private readonly ISecretProvider secrets;

    public FfpManagerBridgeClient(
        HttpClient httpClient,
        IOptions<FfpManagerBridgeOptions> options,
        ISecretProvider secrets)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
        this.secrets = secrets;
    }

    public async Task<ExternalApplicationReceipt?> FindApplicationAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, $"api/applications/by-request/{requestId:D}", cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        // 204 means an authoritative lookup found no record. A 404 may mean an old bridge: fail closed.
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<ExternalApplicationReceipt>(response, cancellationToken);
    }

    public async Task<ExternalReturnOutboxStatus> GetReturnOutboxAsync(CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "api/integration-outbox", cancellationToken);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<ExternalReturnOutboxStatus>(response, cancellationToken);
    }

    public async Task<ExternalApplicationReference> SubmitApplicationAsync(
        ExternalApplicationSubmission submission,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Post, "api/applications", cancellationToken);
        request.Content = JsonContent.Create(submission);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<ExternalApplicationReference>(response, cancellationToken);
    }

    public async Task UploadArtifactAsync(
        string applicationId,
        ExternalArtifactDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        string path = $"api/applications/{Uri.EscapeDataString(applicationId)}/artifacts/{descriptor.PortalDocumentId:D}";
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Put, path, cancellationToken);
        using MultipartFormDataContent multipart = new();
        using StringContent metadata = new(JsonSerializer.Serialize(descriptor));
        metadata.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        multipart.Add(metadata, "metadata");
        StreamContent file = new(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(descriptor.ContentType);
        multipart.Add(file, "file", descriptor.FileName);
        request.Content = multipart;
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<ExternalApplicationStatus> GetStatusAsync(
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        string path = $"api/applications/{Uri.EscapeDataString(applicationId)}/status";
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Get, path, cancellationToken);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<ExternalApplicationStatus>(response, cancellationToken);
    }

    public async Task SendInformationResponseAsync(
        ExternalInformationResponse responsePayload,
        CancellationToken cancellationToken = default)
    {
        string path = $"api/applications/{Uri.EscapeDataString(responsePayload.ApplicationId)}/information-responses";
        using HttpRequestMessage request = await CreateRequestAsync(HttpMethod.Post, path, cancellationToken);
        request.Content = JsonContent.Create(responsePayload);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(method, path);
        string secretName = string.IsNullOrWhiteSpace(options.SharedSecretName)
            ? "FfpManagerIntegration:SharedSecret"
            : options.SharedSecretName;
        string? sharedSecret = await secrets.GetAsync(secretName, cancellationToken);
        if (!string.IsNullOrWhiteSpace(sharedSecret))
        {
            request.Headers.TryAddWithoutValidation("X-FFP-Integration-Key", sharedSecret);
        }

        return request;
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException($"FFP Manager bridge returned an empty {typeof(T).Name} response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        // Read only structured bridge failures and trusted validation field names.
        // Never persist arbitrary remote exception messages or submitted field values.
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is "application/problem+json" or "application/json")
        {
            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Object)
                    {
                        var rejected = new List<string>();
                        foreach (JsonProperty field in errors.EnumerateObject().Take(8))
                        {
                            // Field identifiers from the bridge are not trusted as display text.
                            string name = field.Name switch
                            {
                                "PrimaryTelephone" => "Primary telephone",
                                "SecondaryTelephone" => "Secondary telephone",
                                "Parish" => "Parish",
                                "FirstName" => "First name",
                                "LastName" => "Last name",
                                "DateOfBirth" => "Date of birth",
                                "StreetAddress" => "Street address",
                                "ApplicantGender" => "Applicant gender",
                                "ApplicantMaritalStatus" => "Marital status",
                                "Occupation" => "Occupation",
                                _ => "Unrecognized submission field"
                            };
                            string reason = "rejected by destination validation";
                            if (field.Value.ValueKind == JsonValueKind.Array &&
                                field.Value.GetArrayLength() > 0 &&
                                field.Value[0].ValueKind == JsonValueKind.String)
                            {
                                string message = field.Value[0].GetString() ?? "";
                                if (message == "The applicant telephone requires correction before transfer.")
                                    reason = "requires correction before transfer";
                                else if (message.Contains("required", StringComparison.OrdinalIgnoreCase))
                                    reason = "is required";
                                else if (message.Contains("invalid", StringComparison.OrdinalIgnoreCase))
                                    reason = "has an invalid value";
                            }
                            rejected.Add(name + ": " + reason);
                        }
                        string diagnosis = rejected.Count == 0
                            ? "The destination rejected the submission fields without a specific field name."
                            : "The destination rejected: " + string.Join("; ", rejected) + ".";
                        string code = errors.TryGetProperty("PrimaryTelephone", out _) ? "VALIDATION_PRIMARY_TELEPHONE"
                            : errors.TryGetProperty("SecondaryTelephone", out _) ? "VALIDATION_SECONDARY_TELEPHONE"
                            : errors.TryGetProperty("Parish", out _) ? "VALIDATION_PARISH" : "VALIDATION";
                        var failure = TransferFailures.For("BridgeValidation", code) with { Explanation = diagnosis };
                        throw new IntegrationTransferException(failure);
                    }
                    // Structured, classified bridge failures are safe only after code
                    // and diagnostic stage have been validated against our contract.
                    if (root.TryGetProperty("code", out JsonElement codeElement) && codeElement.ValueKind == JsonValueKind.String)
                    {
                        string code = codeElement.GetString() ?? "";
                        if (code.Length is > 0 and <= 40 &&
                            code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_'))
                        {
                            var failure = TransferFailures.For("Bridge", code);
                            if (code.StartsWith("SQL_", StringComparison.Ordinal) &&
                                int.TryParse(code.AsSpan(4), out int number))
                                failure = TransferFailures.Sql("SqlServer", number);
                            throw new IntegrationTransferException(failure);
                        }
                    }
                }
            }
            catch (JsonException) { /* Not structured JSON; report transport without logging its content. */ }
        }
        throw new HttpRequestException($"FFP Manager bridge returned HTTP {(int)response.StatusCode}; no recognized structured failure details.", null, response.StatusCode);
    }
}
