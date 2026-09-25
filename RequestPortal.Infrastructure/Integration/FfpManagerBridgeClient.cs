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

        string detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"FFP Manager bridge returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail}",
            null,
            response.StatusCode);
    }
}
