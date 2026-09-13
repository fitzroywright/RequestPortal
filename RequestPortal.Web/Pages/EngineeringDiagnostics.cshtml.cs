using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Common.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Hosting;
using RequestPortal.Common.Diagnostics;

namespace RequestPortal.Web.Pages;

[Authorize]
[IgnoreAntiforgeryToken]
public sealed class EngineeringDiagnosticsModel : PageModel
{
    private readonly EngineeringDiagnosticEngine engine;
    private readonly IEngineeringDiagnosticRunStore store;
    private readonly RequestPortalEngineeringDiagnosticCatalog catalog;
    private readonly IWebHostEnvironment environment;

    public EngineeringDiagnosticsModel(
        EngineeringDiagnosticEngine engine,
        IEngineeringDiagnosticRunStore store,
        RequestPortalEngineeringDiagnosticCatalog catalog,
        IWebHostEnvironment environment)
    {
        this.engine = engine;
        this.store = store;
        this.catalog = catalog;
        this.environment = environment;
    }

    public async Task<IActionResult> OnGetAsync(string? path, int? take, CancellationToken cancellationToken)
    {
        string normalized = (path ?? string.Empty).Trim('/');
        if (string.Equals(normalized, "runs", StringComparison.OrdinalIgnoreCase))
        {
            int requested = Math.Clamp(take ?? 25, 1, 100);
            return new JsonResult(await store.GetRecentAsync(requested, cancellationToken));
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 2 &&
            string.Equals(segments[0], "runs", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(segments[1], out Guid runId))
        {
            EngineeringDiagnosticRun? run = await store.GetAsync(runId, cancellationToken);
            return run is null ? NotFound() : new JsonResult(run);
        }

        return NotFound();
    }

    public async Task<IActionResult> OnPostAsync(string? path, CancellationToken cancellationToken)
    {
        string normalized = (path ?? string.Empty).Trim('/');
        if (string.Equals(normalized, "run", StringComparison.OrdinalIgnoreCase))
        {
            EngineeringDiagnosticRunRequest? request = await JsonSerializer.DeserializeAsync<EngineeringDiagnosticRunRequest>(
                Request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            if (request is null)
            {
                return BadRequest(new { error = "Diagnostic request body is required." });
            }

            try
            {
                string requestedBy = User.Identity?.Name ?? "authenticated-operator";
                EngineeringDiagnosticRun run = await engine.RunAsync(
                    request.Level,
                    "RequestPortal",
                    environment.EnvironmentName,
                    requestedBy,
                    request.Reason,
                    catalog.Build(),
                    cancellationToken);
                return new JsonResult(run);
            }
            catch (ArgumentException exception)
            {
                return BadRequest(new { error = exception.Message });
            }
        }

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 3 &&
            string.Equals(segments[0], "runs", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(segments[1], out Guid runId) &&
            string.Equals(segments[2], "resolve", StringComparison.OrdinalIgnoreCase))
        {
            EngineeringDiagnosticResolutionRequest? request = await JsonSerializer.DeserializeAsync<EngineeringDiagnosticResolutionRequest>(
                Request.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);

            if (request is null || string.IsNullOrWhiteSpace(request.Resolution))
            {
                return BadRequest(new { error = "Resolution is required." });
            }

            string resolvedBy = User.Identity?.Name ?? "authenticated-operator";
            EngineeringDiagnosticRun? resolved = await store.ResolveAsync(
                runId,
                resolvedBy,
                request.Resolution.Trim(),
                cancellationToken);
            return resolved is null ? NotFound() : new JsonResult(resolved);
        }

        return NotFound();
    }
}
