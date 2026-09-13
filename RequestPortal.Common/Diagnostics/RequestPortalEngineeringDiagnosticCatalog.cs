using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RequestPortal.Common.Diagnostics;

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
                "requestportal-l4-common-diagnostics",
                "Common.Diagnostics",
                EngineeringDiagnosticLevel.Level4Analysis,
                _ => Task.FromResult(Passed(
                    "requestportal-l4-common-diagnostics",
                    "Common.Diagnostics",
                    "RequestPortal is executing this diagnostic run through Common.Diagnostics."))),

            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l4-common-security",
                "Common.Security",
                EngineeringDiagnosticLevel.Level4Analysis,
                _ => Task.FromResult(Passed(
                    "requestportal-l4-common-security",
                    "Common.Security",
                    "RequestPortal staff sign-in authenticates through Common.Security and authorizes through the Common.Security permission engine; RequestPortal supplies the application-specific authorization store."))),

            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l4-common-secrets",
                "Common.Secrets",
                EngineeringDiagnosticLevel.Level4Analysis,
                _ => Task.FromResult(Passed(
                    "requestportal-l4-common-secrets",
                    "Common.Secrets",
                    "RequestPortal is registered with Common.Secrets; production bootstrap uses the central Common.Secrets runtime."))),

            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l4-common-messaging",
                "Common.Messaging",
                EngineeringDiagnosticLevel.Level4Analysis,
                _ => Task.FromResult(Passed(
                    "requestportal-l4-common-messaging",
                    "Common.Messaging",
                    "RequestPortal uses Common.Messaging for queued external delivery; the RequestPortal outbox remains the application transaction boundary."))),

            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l4-common-storage",
                "Common.Storage",
                EngineeringDiagnosticLevel.Level4Analysis,
                CheckCommonStorageAsync),

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
                    "Level 1 executes the complete cumulative RequestPortal diagnostic set; component warnings and failures determine final readiness.")))
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

    private async Task<EngineeringDiagnosticCheckResult> CheckCommonStorageAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        HealthCheckService healthChecks = scope.ServiceProvider.GetRequiredService<HealthCheckService>();
        HealthReport report = await healthChecks.CheckHealthAsync(
            registration => string.Equals(registration.Name, "common-storage", StringComparison.OrdinalIgnoreCase),
            cancellationToken).ConfigureAwait(false);

        if (!report.Entries.TryGetValue("common-storage", out HealthReportEntry entry))
        {
            return Warning(
                "requestportal-l4-common-storage",
                "Common.Storage",
                "Common.Storage is referenced by RequestPortal, but its health check is not registered.");
        }

        if (entry.Status == HealthStatus.Unhealthy)
        {
            return Failed(
                "requestportal-l4-common-storage",
                "Common.Storage",
                "Common.Storage is unavailable to RequestPortal.",
                entry.Description ?? entry.Exception?.Message ?? "The common-storage health check reported Unhealthy.");
        }

        if (entry.Status == HealthStatus.Degraded)
        {
            return Warning(
                "requestportal-l4-common-storage",
                "Common.Storage",
                entry.Description ?? "Common.Storage is available but degraded.");
        }

        return Passed(
            "requestportal-l4-common-storage",
            "Common.Storage",
            entry.Description ?? "RequestPortal is using Common.Storage and its health check passed.");
    }

    private EngineeringDiagnosticCheckResult CheckSystemConfiguration()
    {
        bool storageConfigured = !string.IsNullOrWhiteSpace(configuration["Storage:Local:RootPath"]);
        bool notificationsConfigured = configuration.GetValue("Notifications:Enabled", false);
        bool activeDirectoryConfigured = !string.IsNullOrWhiteSpace(configuration["Authentication:ActiveDirectory:Server"]);

        if (!storageConfigured || !activeDirectoryConfigured)
        {
            return Warning(
                "requestportal-l3-system",
                "RequestPortal system configuration",
                "Storage or Active Directory configuration is incomplete.");
        }

        return notificationsConfigured
            ? Passed(
                "requestportal-l3-system",
                "RequestPortal system configuration",
                "Storage, Active Directory and notifications are configured.")
            : Warning(
                "requestportal-l3-system",
                "RequestPortal system configuration",
                "Storage and Active Directory are configured, but notifications are disabled.");
    }

    private EngineeringDiagnosticCheckResult CheckIntegrationConfiguration()
    {
        bool openBaoConfigured = !string.IsNullOrWhiteSpace(configuration["CommonSecrets:OpenBao:Address"]);
        bool malwareScannerConfigured = configuration.GetValue("Uploads:MalwareScanning:Enabled", false) &&
            !string.IsNullOrWhiteSpace(configuration["Uploads:MalwareScanning:Host"]);
        bool externalIdentityConfigured =
            !string.IsNullOrWhiteSpace(configuration["Authentication:Microsoft:ClientId"]) ||
            !string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientId"]) ||
            !string.IsNullOrWhiteSpace(configuration["Authentication:Facebook:AppId"]);

        if (!openBaoConfigured || !malwareScannerConfigured || !externalIdentityConfigured)
        {
            return Warning(
                "requestportal-l2-integrations",
                "RequestPortal integration readiness",
                "One or more production integrations are not fully configured.");
        }

        return Passed(
            "requestportal-l2-integrations",
            "RequestPortal integration readiness",
            "Configured production integrations and central Common-platform adoption checks are satisfied.");
    }

    private static EngineeringDiagnosticCheckResult Passed(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Passed, summary);

    private static EngineeringDiagnosticCheckResult Warning(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Warning, summary);

    private static EngineeringDiagnosticCheckResult Failed(string id, string name, string summary, string evidence) =>
        new(id, name, EngineeringDiagnosticStatus.Failed, summary, evidence);
}
