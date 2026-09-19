using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Common.Registration;

namespace RequestPortal.Web;

public sealed class OperationsTelemetryPublisher(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<OperationsTelemetryPublisher> logger) : BackgroundService
{
    private const string ApplicationId = "RequestPortal";
    private const string DisplayName = "Request Portal";
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? operationsUrl = configuration["Aegis:Operations:Url"];
        if (string.IsNullOrWhiteSpace(operationsUrl))
        {
            logger.LogInformation("Operations telemetry is disabled because Aegis:Operations:Url is not configured.");
            return;
        }

        string instanceId = configuration["Service:Identity"]?.Trim() ?? Environment.MachineName;
        string identityFile = configuration["Aegis:Registration:IdentityFile"]
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aegis", "RequestPortal", "registration-identity.json")
                : "/var/lib/aegis/requestportal/registration-identity.json");

        var identityStore = new FileRegistrationIdentityStore(identityFile);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RegistrationIdentityDocument identity =
                    await identityStore.LoadOrCreateAsync(ApplicationId, instanceId, stoppingToken);

                if (string.IsNullOrWhiteSpace(identity.Credential))
                {
                    logger.LogDebug("Operations telemetry not published because the application registration has no durable credential yet.");
                }
                else
                {
                    using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
                    using var request = new HttpRequestMessage(
                        HttpMethod.Post,
                        operationsUrl.TrimEnd('/') + "/api/operations/applications/observe")
                    {
                        Content = JsonContent.Create(new
                        {
                            applicationId = ApplicationId,
                            displayName = DisplayName,
                            instanceId,
                            environment = environment.EnvironmentName,
                            version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                            registrationStatus = "Registered",
                            state = "Unknown",
                            observedAtUtc = DateTimeOffset.UtcNow,
                            configurationStatus = "Published",
                            dependencies = Array.Empty<string>(),
                            reason = "Process heartbeat received; aggregate health requires specialized diagnostic evidence."
                        })
                    };
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
                    request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", Guid.NewGuid().ToString("N"));

                    using HttpResponseMessage response = await client.SendAsync(request, stoppingToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        logger.LogDebug(
                            "Operations heartbeat returned HTTP {StatusCode}; application remains operational.",
                            (int)response.StatusCode);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Operations telemetry heartbeat failed; application remains operational.");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
