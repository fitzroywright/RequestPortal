using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Diagnostics;

namespace RequestPortal.Infrastructure.Integration;

public sealed class FfpManagerBridgeStartupDiagnostic : IStartupDiagnostic
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IOptions<FfpManagerBridgeOptions> options;

    public FfpManagerBridgeStartupDiagnostic(
        IHttpClientFactory httpClientFactory,
        IOptions<FfpManagerBridgeOptions> options)
    {
        this.httpClientFactory = httpClientFactory;
        this.options = options;
    }

    public string Name => "FFP Manager Bridge";

    public async Task<DiagnosticCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        FfpManagerBridgeOptions current = options.Value;
        if (!current.Enabled)
        {
            return new DiagnosticCheckResult(Name, true, "Integration is disabled.");
        }

        if (!Uri.TryCreate(current.BaseUrl, UriKind.Absolute, out Uri? baseUri))
        {
            return new DiagnosticCheckResult(Name, false, "FfpManagerIntegration:BaseUrl is not a valid absolute URL.");
        }

        try
        {
            HttpClient client = httpClientFactory.CreateClient("FfpManagerBridgeHealth");
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(baseUri, "health"));
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DiagnosticCheckResult(
                    Name,
                    false,
                    $"Bridge health endpoint returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            BridgeHealthResponse? health = await response.Content.ReadFromJsonAsync<BridgeHealthResponse>(
                cancellationToken: cancellationToken);
            if (health is null || !string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new DiagnosticCheckResult(Name, false, "Bridge health response was empty or unhealthy.");
            }

            return new DiagnosticCheckResult(
                Name,
                true,
                $"Reachable. Gateway={health.Gateway ?? "unknown"}; MaxArtifactBytes={health.MaxArtifactBytes?.ToString() ?? "unknown"}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DiagnosticCheckResult(Name, false, exception.Message);
        }
    }

    private sealed record BridgeHealthResponse(string Status, string? Gateway, long? MaxArtifactBytes);
}
