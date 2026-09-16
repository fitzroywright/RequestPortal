using System.Reflection;
using Common.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using RequestPortal.Common.Diagnostics;

namespace RequestPortal.Web.Pages;

[AllowAnonymous]
public sealed class HealthModel : PageModel
{
    private readonly IEnumerable<IStartupDiagnostic> diagnostics;
    private readonly IConfiguration configuration;
    private readonly IHostEnvironment environment;

    public HealthModel(
        IEnumerable<IStartupDiagnostic> diagnostics,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        this.diagnostics = diagnostics;
        this.configuration = configuration;
        this.environment = environment;
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        List<string> failedCriticalChecks = [];

        foreach (IStartupDiagnostic diagnostic in diagnostics)
        {
            DiagnosticCheckResult result = await diagnostic.CheckAsync(cancellationToken);
            if (!result.IsHealthy)
            {
                failedCriticalChecks.Add(result.Name);
            }
        }

        AegisHealthState state;
        string summary;

        if (failedCriticalChecks.Count > 0)
        {
            state = AegisHealthState.Unhealthy;
            summary = $"Critical dependency check failed: {string.Join(", ", failedCriticalChecks)}.";
        }
        else
        {
            List<string> controlPlaneGaps = [];
            if (string.IsNullOrWhiteSpace(configuration["Aegis:Configuration:Url"]))
                controlPlaneGaps.Add("Configuration URL");
            if (string.IsNullOrWhiteSpace(configuration["Aegis:Diagnostics:Url"]))
                controlPlaneGaps.Add("Diagnostics URL");

            if (controlPlaneGaps.Count > 0)
            {
                state = AegisHealthState.Degraded;
                summary = $"Application is operational but control-plane configuration is incomplete: {string.Join(", ", controlPlaneGaps)}.";
            }
            else
            {
                state = AegisHealthState.Healthy;
                summary = "Application and critical dependencies are healthy.";
            }
        }

        string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var report = new AegisHealthReport(
            state.ToString(),
            "RequestPortal",
            version,
            environment.EnvironmentName,
            Environment.MachineName,
            DateTimeOffset.UtcNow,
            summary);

        return new JsonResult(report)
        {
            StatusCode = state == AegisHealthState.Unhealthy
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status200OK
        };
    }
}
