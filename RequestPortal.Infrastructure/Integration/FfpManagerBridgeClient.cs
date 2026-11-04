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
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Do not persist or log remote exception bodies, SQL details or applicant payloads.
        if(response.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if(document.RootElement.TryGetProperty("errors",out var errors) && errors.TryGetProperty("Parish",out _))
                    throw new IntegrationTransferException(TransferFailures.For("Bridge", "VALIDATION_PARISH"));
            }
            catch(JsonException) { }
        }
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                var failure = await response.Content.ReadFromJsonAsync<TransferFailure>(cancellationToken: cancellationToken);
                if (failure?.Code is { } code)
                    throw new IntegrationTransferException(TransferFailures.For("Bridge", code));
            }
            catch (JsonException) { }
        }
        throw new HttpRequestException($"FFP Manager bridge returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }
}
