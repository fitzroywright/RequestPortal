using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Diagnostics;
using Common.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RequestPortal.Common.Diagnostics;

public sealed class RequestPortalEngineeringDiagnosticCatalog
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IConfiguration configuration;
    private readonly ISecretProvider secrets;
    private readonly ICommonSecretsHealthCheck secretsHealth;

    public RequestPortalEngineeringDiagnosticCatalog(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ISecretProvider secrets,
        ICommonSecretsHealthCheck secretsHealth)
    {
        this.scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        this.secretsHealth = secretsHealth ?? throw new ArgumentNullException(nameof(secretsHealth));
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
                CheckIntegrationConfigurationAsync),

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
        bool durableMessagingConfigured = !string.IsNullOrWhiteSpace(configuration["Messaging:DurableQueuePath"]);
        bool notificationsConfigured =
            configuration.GetValue("Notifications:Enabled", false) &&
            !string.IsNullOrWhiteSpace(configuration["Notifications:SmtpHost"]) &&
            !string.IsNullOrWhiteSpace(configuration["Notifications:FromAddress"]);

        string[] reviewerGroups = configuration.GetSection("Authentication:ActiveDirectory:ReviewerGroups").Get<string[]>() ?? Array.Empty<string>();
        string[] administratorGroups = configuration.GetSection("Authentication:ActiveDirectory:AdministratorGroups").Get<string[]>() ?? Array.Empty<string>();
        bool staffOidcConfigured =
            configuration.GetValue("Authentication:StaffOidc:Enabled", false) &&
            !string.IsNullOrWhiteSpace(configuration["Authentication:StaffOidc:Authority"]) &&
            !string.IsNullOrWhiteSpace(configuration["Authentication:StaffOidc:ClientId"]) &&
            reviewerGroups.Length > 0 &&
            administratorGroups.Length > 0;

        List<string> missing = [];
        if (!storageConfigured) missing.Add("durable storage");
        if (!durableMessagingConfigured) missing.Add("durable messaging");
        if (!notificationsConfigured) missing.Add("SMTP notifications");
        if (!staffOidcConfigured) missing.Add("staff OIDC/group mapping");

        if (missing.Count > 0)
        {
            return Warning(
                "requestportal-l3-system",
                "RequestPortal system configuration",
                $"Production configuration is incomplete: {string.Join(", ", missing)}.");
        }

        return Passed(
            "requestportal-l3-system",
            "RequestPortal system configuration",
            "Durable storage, durable messaging, SMTP notifications and staff OIDC/group mappings are configured.");
    }

    private async Task<EngineeringDiagnosticCheckResult> CheckIntegrationConfigurationAsync(
        CancellationToken cancellationToken)
    {
        CommonSecretsHealth secretHealth =
            await secretsHealth.CheckAsync(cancellationToken);
        bool secretsConfigured = secretHealth.IsConfigured && secretHealth.IsAvailable;
        bool malwareScannerConfigured = configuration.GetValue("Uploads:MalwareScanning:Enabled", false) &&
            !string.IsNullOrWhiteSpace(configuration["Uploads:MalwareScanning:Host"]);
        bool externalIdentityConfigured =
            HasPair("Authentication:Microsoft:ClientId", "Authentication:Microsoft:ClientSecret") ||
            HasPair("Authentication:Google:ClientId", "Authentication:Google:ClientSecret") ||
            HasPair("Authentication:Facebook:AppId", "Authentication:Facebook:AppSecret");

        bool ffpManagerEnabled = configuration.GetValue("FfpManagerIntegration:Enabled", false);
        string ffpSecretName =
            configuration["FfpManagerIntegration:SharedSecretName"]?.Trim()
            ?? "FfpManagerIntegration:SharedSecret";
        string? ffpSecret = ffpManagerEnabled
            ? await secrets.GetAsync(ffpSecretName, cancellationToken)
            : null;
        bool ffpManagerConfigured = !ffpManagerEnabled ||
            (Uri.TryCreate(
                 configuration["FfpManagerIntegration:BaseUrl"],
                 UriKind.Absolute,
                 out Uri? bridgeUri) &&
             bridgeUri.Scheme == Uri.UriSchemeHttps &&
             !string.IsNullOrWhiteSpace(ffpSecret));

        List<string> missing = [];
        if (!secretsConfigured) missing.Add($"Common.Secrets provider ({secretHealth.ProviderName}: {secretHealth.Message})");
        if (!malwareScannerConfigured) missing.Add("malware scanner");
        if (!externalIdentityConfigured) missing.Add("representative identity provider");
        if (!ffpManagerConfigured) missing.Add("FFP Manager adapter");

        if (missing.Count > 0)
        {
            return Warning(
                "requestportal-l2-integrations",
                "RequestPortal integration readiness",
                $"One or more production integrations are not fully configured: {string.Join(", ", missing)}.");
        }

        return Passed(
            "requestportal-l2-integrations",
            "RequestPortal integration readiness",
            "Common.Secrets, malware scanning, representative identity and commissioned adapter configuration checks are satisfied.");
    }

    private bool HasPair(string firstKey, string secondKey)
    {
        return !string.IsNullOrWhiteSpace(configuration[firstKey]) &&
               !string.IsNullOrWhiteSpace(configuration[secondKey]);
    }

    private static EngineeringDiagnosticCheckResult Passed(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Passed, summary);

    private static EngineeringDiagnosticCheckResult Warning(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Warning, summary);

    private static EngineeringDiagnosticCheckResult Failed(string id, string name, string summary, string evidence) =>
        new(id, name, EngineeringDiagnosticStatus.Failed, summary, evidence);
}
