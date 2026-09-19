using RequestPortal.Common.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using Common.Registration;

namespace RequestPortal.Web;

public sealed class OperationsTelemetryPublisher(
    IConfiguration configuration,
    IHostEnvironment environment,
    IServiceProvider services,
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
                    IReadOnlyList<DiagnosticCheckResult>? diagnosticResults = null;
                    try
                    {
                        await using AsyncServiceScope scope = services.CreateAsyncScope();
                        StartupDiagnosticRunner runner = scope.ServiceProvider.GetRequiredService<StartupDiagnosticRunner>();
                        diagnosticResults = await runner.RunAsync(stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogDebug(ex, "Request Portal diagnostic publication could not collect current evidence.");
                    }

                    string operationalState = diagnosticResults is null
                        ? "Unknown"
                        : diagnosticResults.All(x => x.IsHealthy) ? "Healthy" : "Failed";

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
                            state = operationalState,
                            observedAtUtc = DateTimeOffset.UtcNow,
                            configurationStatus = "Published",
                            dependencies = Array.Empty<string>(),
                            reason = diagnosticResults is null
                                ? "Process heartbeat received but diagnostic evidence could not be collected."
                                : "Aggregate state is based on the Request Portal startup diagnostic set."
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

                    await PublishFlowDefinitionAsync(
                        client,
                        operationsUrl,
                        identity,
                        stoppingToken);

                    if (diagnosticResults is not null)
                    {
                        await PublishDiagnosticsAsync(
                            client,
                            operationsUrl,
                            identity,
                            diagnosticResults,
                            stoppingToken);
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

    private static async Task PublishFlowDefinitionAsync(
        HttpClient client,
        string operationsUrl,
        RegistrationIdentityDocument identity,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            operationsUrl.TrimEnd('/') + "/api/operations/flow-definitions")
        {
            Content = JsonContent.Create(new
            {
                applicationId = ApplicationId,
                flowType = "RequestSubmission",
                displayName = "Request Submission",
                stages = new object[]
                {
                new { name = "Applicant", parentStage = (string?)null, branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                new { name = "Validation", parentStage = "Applicant", branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                new { name = "Documents", parentStage = "Validation", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 },
                new { name = "Submission", parentStage = "Documents", branch = (string?)null, warningAfterSeconds = 10, criticalAfterSeconds = 60 },
                new { name = "Processing", parentStage = "Submission", branch = (string?)null, warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                new { name = "Downstream", parentStage = "Processing", branch = (string?)null, warningAfterSeconds = 60, criticalAfterSeconds = 600 },
                new { name = "Acknowledgement", parentStage = "Downstream", branch = (string?)null, warningAfterSeconds = 30, criticalAfterSeconds = 300 }
                },
                updatedAtUtc = DateTimeOffset.UtcNow
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
        request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
        request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", Guid.NewGuid().ToString("N"));

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Flow metadata is advisory to Operations and must never affect application availability.
        }
    }


    private static async Task PublishDiagnosticsAsync(
        HttpClient client,
        string operationsUrl,
        RegistrationIdentityDocument identity,
        IReadOnlyList<DiagnosticCheckResult> diagnostics,
        CancellationToken ct)
    {
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        foreach (DiagnosticCheckResult check in diagnostics)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                operationsUrl.TrimEnd('/') + "/api/operations/diagnostics/observe")
            {
                Content = JsonContent.Create(new
                {
                    diagnosticId = ApplicationId + ":" + check.Name,
                    applicationId = ApplicationId,
                    component = check.Name,
                    category = "Health",
                    state = check.IsHealthy ? "Healthy" : "Failed",
                    summary = check.Detail,
                    observedAtUtc = observedAt,
                    detail = (string?)null,
                    runbook = "docs/RUNBOOK.md"
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", identity.Credential);
            request.Headers.TryAddWithoutValidation("X-Aegis-Application-Id", identity.ApplicationId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Instance-Id", identity.InstanceId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Installation-Id", identity.InstallationId);
            request.Headers.TryAddWithoutValidation("X-Aegis-Correlation-Id", Guid.NewGuid().ToString("N"));

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                break;
        }
    }

}
