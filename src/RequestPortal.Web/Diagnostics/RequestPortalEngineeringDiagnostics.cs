using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RequestPortal.Common.Diagnostics;

namespace RequestPortal.Web.Diagnostics;

public static class RequestPortalEngineeringDiagnostics
{
    public static IServiceCollection AddRequestPortalEngineeringDiagnostics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddCommonDiagnostics();
        services.AddSingleton<RequestPortalEngineeringDiagnosticCatalog>();
        return services;
    }

    public static IEndpointRouteBuilder MapRequestPortalEngineeringDiagnostics(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health", () => Results.Ok(new
        {
            status = "running",
            application = "RequestPortal",
            timeUtc = DateTimeOffset.UtcNow
        })).AllowAnonymous();

        RouteGroupBuilder group = endpoints.MapGroup("/api/engineering/diagnostics").RequireAuthorization();

        group.MapGet("/runs", async (int? take, IEngineeringDiagnosticRunStore store, CancellationToken cancellationToken) =>
        {
            int requested = Math.Clamp(take ?? 25, 1, 100);
            return Results.Ok(await store.GetRecentAsync(requested, cancellationToken));
        });

        group.MapGet("/runs/{runId:guid}", async (Guid runId, IEngineeringDiagnosticRunStore store, CancellationToken cancellationToken) =>
        {
            EngineeringDiagnosticRun? run = await store.GetAsync(runId, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        group.MapPost("/run", async (
            EngineeringDiagnosticRunRequest request,
            EngineeringDiagnosticEngine engine,
            RequestPortalEngineeringDiagnosticCatalog catalog,
            IWebHostEnvironment environment,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            try
            {
                string requestedBy = context.User.Identity?.Name ?? "authenticated-operator";
                EngineeringDiagnosticRun run = await engine.RunAsync(
                    request.Level,
                    "RequestPortal",
                    environment.EnvironmentName,
                    requestedBy,
                    request.Reason,
                    catalog.Build(),
                    cancellationToken);
                return Results.Ok(run);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        group.MapPost("/runs/{runId:guid}/resolve", async (
            Guid runId,
            EngineeringDiagnosticResolutionRequest request,
            IEngineeringDiagnosticRunStore store,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Resolution))
            {
                return Results.BadRequest(new { error = "Resolution is required." });
            }

            string resolvedBy = context.User.Identity?.Name ?? "authenticated-operator";
            EngineeringDiagnosticRun? resolved = await store.ResolveAsync(
                runId,
                resolvedBy,
                request.Resolution.Trim(),
                cancellationToken);
            return resolved is null ? Results.NotFound() : Results.Ok(resolved);
        });

        return endpoints;
    }
}

public sealed class RequestPortalEngineeringDiagnosticCatalog
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IConfiguration configuration;

    public RequestPortalEngineeringDiagnosticCatalog(IServiceScopeFactory scopeFactory, IConfiguration configuration)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public IReadOnlyList<EngineeringDiagnosticCheckDefinition> Build()
    {
        return
        [
            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l5-process",
                "RequestPortal application process",
                EngineeringDiagnosticLevel.Level5Scan,
                _ => Task.FromResult(Passed(
                    "requestportal-l5-process",
                    "RequestPortal application process",
                    "RequestPortal web application is running."))),

            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l4-components",
                "RequestPortal database and directory components",
                EngineeringDiagnosticLevel.Level4Analysis,
                CheckStartupComponentsAsync),

            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l3-system",
                "RequestPortal system configuration",
                EngineeringDiagnosticLevel.Level3Verification,
                _ => Task.FromResult(CheckSystemConfiguration())),

            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l2-integrations",
                "RequestPortal integration readiness",
                EngineeringDiagnosticLevel.Level2Repair,
                _ => Task.FromResult(CheckIntegrationConfiguration())),

            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l1-full-system",
                "RequestPortal full-system readiness",
                EngineeringDiagnosticLevel.Level1CriticalIntervention,
                _ => Task.FromResult(Passed(
                    "requestportal-l1-full-system",
                    "RequestPortal full-system readiness",
                    "Level 1 executes the complete cumulative RequestPortal diagnostic set and intervention gate.")))
        ];
    }

    private async Task<EngineeringDiagnosticCheckResult> CheckStartupComponentsAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        StartupDiagnosticRunner runner = scope.ServiceProvider.GetRequiredService<StartupDiagnosticRunner>();
        IReadOnlyList<DiagnosticCheckResult> results = await runner.RunAsync(cancellationToken);
        DiagnosticCheckResult[] failed = results.Where(result => !result.IsHealthy).ToArray();

        if (failed.Length == 0)
        {
            string healthy = string.Join(", ", results.Select(result => result.Name));
            return Passed(
                "requestportal-l4-components",
                "RequestPortal database and directory components",
                $"Startup component diagnostics passed: {healthy}.");
        }

        string evidence = string.Join("; ", failed.Select(result => $"{result.Name}: {result.Detail}"));
        return Failed(
            "requestportal-l4-components",
            "RequestPortal database and directory components",
            "One or more startup component diagnostics failed.",
            evidence);
    }

    private EngineeringDiagnosticCheckResult CheckSystemConfiguration()
    {
        bool storageConfigured = !string.IsNullOrWhiteSpace(configuration["Storage:Local:RootPath"]);
        bool notificationsConfigured = configuration.GetValue("Notifications:Enabled", false);
        bool adConfigured = !string.IsNullOrWhiteSpace(configuration["Authentication:ActiveDirectory:Server"]);

        return storageConfigured && adConfigured
            ? Passed(
                "requestportal-l3-system",
                "RequestPortal system configuration",
                notificationsConfigured
                    ? "Storage, Active Directory and notifications are configured."
                    : "Storage and Active Directory are configured; notifications are currently disabled.")
            : Warning(
                "requestportal-l3-system",
                "RequestPortal system configuration",
                "Storage or Active Directory configuration is incomplete.");
    }

    private EngineeringDiagnosticCheckResult CheckIntegrationConfiguration()
    {
        bool openBaoConfigured = !string.IsNullOrWhiteSpace(configuration["OpenBao:Address"]);
        bool malwareScannerConfigured = configuration.GetValue("Uploads:MalwareScanning:Enabled", false) &&
            !string.IsNullOrWhiteSpace(configuration["Uploads:MalwareScanning:Host"]);
        bool externalIdentityConfigured =
            !string.IsNullOrWhiteSpace(configuration["Authentication:Microsoft:ClientId"]) ||
            !string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientId"]) ||
            !string.IsNullOrWhiteSpace(configuration["Authentication:Facebook:AppId"]);

        return openBaoConfigured && malwareScannerConfigured && externalIdentityConfigured
            ? Passed(
                "requestportal-l2-integrations",
                "RequestPortal integration readiness",
                "OpenBao, malware scanning and at least one external identity provider are configured.")
            : Warning(
                "requestportal-l2-integrations",
                "RequestPortal integration readiness",
                "One or more production integrations are not fully configured.");
    }

    private static EngineeringDiagnosticCheckResult Passed(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Passed, summary);

    private static EngineeringDiagnosticCheckResult Warning(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Warning, summary);

    private static EngineeringDiagnosticCheckResult Failed(string id, string name, string summary, string evidence) =>
        new(id, name, EngineeringDiagnosticStatus.Failed, summary, evidence);
}
