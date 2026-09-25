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
            HttpClient client = httpClientFactory.CreateClient("FfpManagerBridgeReadiness");
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(baseUri, "ready"));
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DiagnosticCheckResult(
                    Name,
                    false,
                    $"Bridge readiness endpoint returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            BridgeReadinessResponse? health = await response.Content.ReadFromJsonAsync<BridgeReadinessResponse>(
                cancellationToken: cancellationToken);
            if (health is null || !string.Equals(health.Status, "ready", StringComparison.OrdinalIgnoreCase))
            {
                return new DiagnosticCheckResult(Name, false, "Bridge readiness response was empty or not ready.");
            }

            return new DiagnosticCheckResult(
                Name,
                true,
                $"Ready. Gateway={health.Gateway ?? "unknown"}; OutboxSchema={health.OutboxSchema?.ToString() ?? "n/a"}; ReferenceDataReady={health.ReferenceDataReady?.ToString() ?? "n/a"}.");
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

    private sealed record BridgeReadinessResponse(string Status, string? Gateway, bool? OutboxSchema, bool? ReferenceDataReady);
}
