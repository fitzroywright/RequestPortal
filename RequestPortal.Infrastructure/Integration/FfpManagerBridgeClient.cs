using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;

namespace RequestPortal.Infrastructure.Integration;

public sealed class FfpManagerBridgeClient : IFfpManagerBridgeClient
{
    private readonly HttpClient httpClient;
    private readonly FfpManagerBridgeOptions options;

    public FfpManagerBridgeClient(HttpClient httpClient, IOptions<FfpManagerBridgeOptions> options)
    {
        this.httpClient = httpClient;
        this.options = options.Value;
    }

    public async Task<FfpManagerApplicationReference> SubmitApplicationAsync(
        FfpManagerApplicationSubmission submission,
        CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "api/applications");
        request.Content = JsonContent.Create(submission);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<FfpManagerApplicationReference>(response, cancellationToken);
    }

    public async Task UploadArtifactAsync(
        string applicationId,
        FfpManagerArtifactDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        string path = $"api/applications/{Uri.EscapeDataString(applicationId)}/artifacts/{descriptor.PortalDocumentId:D}";
        using HttpRequestMessage request = CreateRequest(HttpMethod.Put, path);
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

    public async Task<FfpManagerApplicationStatus> GetStatusAsync(
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        string path = $"api/applications/{Uri.EscapeDataString(applicationId)}/status";
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, path);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadRequiredAsync<FfpManagerApplicationStatus>(response, cancellationToken);
    }

    public async Task SendInformationResponseAsync(
        FfpManagerInformationResponse responsePayload,
        CancellationToken cancellationToken = default)
    {
        string path = $"api/applications/{Uri.EscapeDataString(responsePayload.ApplicationId)}/information-responses";
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path);
        request.Content = JsonContent.Create(responsePayload);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        HttpRequestMessage request = new(method, path);
        if (!string.IsNullOrWhiteSpace(options.SharedSecret))
        {
            request.Headers.TryAddWithoutValidation("X-FFP-Integration-Key", options.SharedSecret);
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
