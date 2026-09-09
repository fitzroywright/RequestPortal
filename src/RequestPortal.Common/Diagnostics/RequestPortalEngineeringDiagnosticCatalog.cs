using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
                _ => Task.FromResult(Warning(
                    "requestportal-l4-common-security",
                    "Common.Security",
                    "RequestPortal still uses RequestPortal.Common.Security and its local Active Directory authenticator; central Common.Security adoption is not complete."))),

            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l4-common-secrets",
                "Common.Secrets",
                EngineeringDiagnosticLevel.Level4Analysis,
                _ => Task.FromResult(Warning(
                    "requestportal-l4-common-secrets",
                    "Common.Secrets",
                    "RequestPortal still uses RequestPortal.Common.Secrets/OpenBao integration; central Common.Secrets adoption is not complete."))),

            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l4-common-messaging",
                "Common.Messaging",
                EngineeringDiagnosticLevel.Level4Analysis,
                _ => Task.FromResult(Warning(
                    "requestportal-l4-common-messaging",
                    "Common.Messaging",
                    "RequestPortal still uses its local outbox/messaging implementation; central Common.Messaging adoption is not complete."))),

            new EngineeringDiagnosticCheckDefinition(
                "requestportal-l4-common-storage",
                "Common.Storage",
                EngineeringDiagnosticLevel.Level4Analysis,
                _ => Task.FromResult(Warning(
                    "requestportal-l4-common-storage",
                    "Common.Storage",
                    "RequestPortal still uses RequestPortal.Common.Storage/LocalFileStorage; central Common.Storage adoption is not complete."))),

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
        bool openBaoConfigured = !string.IsNullOrWhiteSpace(configuration["OpenBao:Address"]);
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

        return Warning(
            "requestportal-l2-integrations",
            "RequestPortal integration readiness",
            "External integrations are configured, but Security, Secrets, Messaging and Storage still use RequestPortal-local implementations rather than the central Common platform.");
    }

    private static EngineeringDiagnosticCheckResult Passed(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Passed, summary);

    private static EngineeringDiagnosticCheckResult Warning(string id, string name, string summary) =>
        new(id, name, EngineeringDiagnosticStatus.Warning, summary);

    private static EngineeringDiagnosticCheckResult Failed(string id, string name, string summary, string evidence) =>
        new(id, name, EngineeringDiagnosticStatus.Failed, summary, evidence);
}
