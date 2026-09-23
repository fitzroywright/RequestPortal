using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Common.Diagnostics;
using Common.Registration;
using Common.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RequestPortal.Common.Configuration;

public sealed class ConfigurationContractPublisher(
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ISecretProvider secrets,
    ILogger<ConfigurationContractPublisher> logger) : BackgroundService
{
    private static readonly TimeSpan retryDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PublishBootstrapAsync(
                configuration,
                environment,
                secrets,
                stoppingToken,
                logger);

            try { await Task.Delay(retryDelay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    public static async Task<bool> PublishBootstrapAsync(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ISecretProvider secrets,
        CancellationToken cancellationToken = default,
        ILogger<ConfigurationContractPublisher>? logger = null)
    {
        string configurationUrl = AegisControlPlaneEndpoints.ResolvePublic(
            configuration,
            AegisControlPlaneService.Configuration,
            logger,
            configurationKey: "Aegis:Configuration:Url");
        string operationsUrl = AegisControlPlaneEndpoints.ResolvePublic(
            configuration,
            AegisControlPlaneService.Operations,
            logger,
            configurationKey: "Aegis:Operations:Url");

        string path = configuration["Aegis:Configuration:ContractPath"] ?? configuration["AegisConfiguration:ContractPath"] ?? configuration["ConfigurationService:ContractPath"] ?? Path.Combine(environment.ContentRootPath, "configuration", "RequestPortal.configuration-contract.json");

        try
        {
            if (!File.Exists(path))
            {
                logger?.LogWarning("Configuration contract was not found at {ContractPath}; RequestPortal will retry publication.", path);
                return false;
            }

            JsonObject contract = await BuildRuntimeContractAsync(
                configuration,
                environment,
                secrets,
                path,
                cancellationToken,
                logger);

            string instanceId =
                contract["instanceId"]?.GetValue<string>()
                ?? Environment.MachineName;
            contract["instanceId"] = instanceId;

            string identityFile =
                configuration["Aegis:Registration:IdentityFile"]
                ?? (OperatingSystem.IsWindows()
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Aegis",
                        "RequestPortal",
                        "registration-identity.json")
                    : "/var/lib/aegis/requestportal/registration-identity.json");

            using HttpClient client = new();
            using HttpClient lifecycleClient = new();
            ILifecycleEventSink lifecycle = CreateLifecycleSink(
                operationsUrl,
                lifecycleClient,
                identityFile,
                instanceId);

            var registration = new RegistrationLifecycleClient(
                client,
                new RegistrationLifecycleOptions(
                    new Uri(configurationUrl.TrimEnd('/') + "/", UriKind.Absolute),
                    "RequestPortal",
                    instanceId,
                    identityFile,
                    TimeSpan.FromSeconds(Math.Clamp(
                        configuration.GetValue("Aegis:Registration:RequestTimeoutSeconds", 30),
                        5,
                        120)),
                    RunbookReference: "docs/RUNBOOK.md"),
                logger: logger,
                lifecycleEventSink: lifecycle);

            RegistrationLifecycleStatus result =
                await registration.StepAsync(
                    new JsonObject
                    {
                        ["displayName"] = "Request Portal",
                        ["module"] = "RequestPortal.Web",
                        ["presentation"] = new JsonObject
                        {
                            ["iconUrl"] = configuration["Aegis:Presentation:IconUrl"],
                            ["shortName"] = "Request",
                            ["accent"] = configuration["Aegis:Presentation:Accent"] ?? "gold"
                        },
                        ["siteId"] = contract["siteId"]?.DeepClone(),
                        ["publicUrl"] = configuration["Aegis:PublicUrl"],
                        ["hostname"] = Environment.MachineName,
                        ["runtimeEnvironment"] = environment.EnvironmentName
                    },
                    cancellationToken);

            if (!result.IsRegistered)
            {
                logger?.LogInformation(
                    "RequestPortal registration state is {RegistrationState}: {RegistrationError}; RequestPortal remains operational.",
                    result.State,
                    result.Error ?? "No additional detail.");
                return false;
            }

            System.Net.HttpStatusCode contractStatus =
                await registration.PublishConfigurationContractAsync(contract, cancellationToken);

            if ((int)contractStatus is >= 200 and < 300)
            {
                logger?.LogInformation(
                    "RequestPortal registration is valid and its commissioning contract was published.");
                return true;
            }

            logger?.LogWarning(
                "RequestPortal is registered, but Configuration contract publication returned HTTP {StatusCode}.",
                (int)contractStatus);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Unable to publish RequestPortal commissioning state; RequestPortal will retry publication.");
            return false;
        }
    }

    private static async Task<JsonObject> BuildRuntimeContractAsync(IConfiguration configuration, IWebHostEnvironment environment, ISecretProvider secrets, string path, CancellationToken cancellationToken, ILogger<ConfigurationContractPublisher>? logger)
    {
        JsonObject contract = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))?.AsObject() ?? throw new InvalidDataException("The RequestPortal configuration contract is not a JSON object.");
        contract["version"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? contract["version"]?.GetValue<string>() ?? "unknown";
        string? siteId = configuration["Site:Id"];
        contract["siteId"] = NullIfWhiteSpace(siteId);
        contract["instanceId"] = NullIfWhiteSpace(configuration["Service:Identity"] ?? Environment.MachineName);
        contract["module"] = "RequestPortal.Web";
        contract["runbookReference"] = "docs/RUNBOOK.md";
        contract["runtimeEnvironment"] = new JsonObject
        {
            ["machineName"] = Environment.MachineName,
            ["operatingSystem"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["frameworkDescription"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["hostingEnvironment"] = environment.EnvironmentName
        };
        contract["presentation"] = new JsonObject
        {
            ["iconUrl"] = configuration["Aegis:Presentation:IconUrl"],
            ["shortName"] = "Request",
            ["accent"] = configuration["Aegis:Presentation:Accent"] ?? "gold"
        };
        string? diagnosticSecretName =
            NullIfWhiteSpace(configuration["Aegis:Diagnostics:RequestCredentialSecretName"]);
        contract["diagnostics"] = new JsonObject
        {
            ["baseUrl"] = NullIfWhiteSpace(configuration["Aegis:PublicUrl"]),
            ["healthPath"] = "/health",
            ["diagnosticsRunPath"] = "/api/engineering/diagnostics/diagnostic-level/run",
            ["diagnosticsRunsPath"] = "/api/engineering/diagnostics/diagnostic-level/runs",
            ["supportsRemoteDiagnostics"] = true,
            ["supportsOperationalTelemetry"] = true,
            ["authenticationScheme"] = "DiagnosticLevel-HMAC-SHA256",
            ["secretName"] = diagnosticSecretName,
            ["supportedLevels"] = new JsonArray(5, 4, 3, 2, 1)
        };

        bool hasExternalProvider =
            (HasValue(configuration, "Authentication:Google:ClientId") && await HasSecretAsync(secrets, "Authentication:Google:ClientSecret", cancellationToken, logger)) ||
            (HasValue(configuration, "Authentication:Microsoft:ClientId") && await HasSecretAsync(secrets, "Authentication:Microsoft:ClientSecret", cancellationToken, logger)) ||
            (HasValue(configuration, "Authentication:Facebook:AppId") && await HasSecretAsync(secrets, "Authentication:Facebook:AppSecret", cancellationToken, logger));
        bool staffGroupsConfigured = HasSectionValues(configuration, "Authentication:ActiveDirectory:ReviewerGroups") && HasSectionValues(configuration, "Authentication:ActiveDirectory:AdministratorGroups");
        bool malwareConfigured = configuration.GetValue("Uploads:MalwareScanning:Enabled", false) && HasValue(configuration, "Uploads:MalwareScanning:Host");
        bool smtpConfigured = configuration.GetValue("Notifications:Enabled", false) && HasValue(configuration, "Notifications:SmtpHost") && HasValue(configuration, "Notifications:FromAddress");
        bool oidcConfigured = configuration.GetValue("Authentication:StaffOidc:Enabled", false) && HasValue(configuration, "Authentication:StaffOidc:Authority") && HasValue(configuration, "Authentication:StaffOidc:ClientId") && await HasSecretAsync(secrets, "Authentication:StaffOidc:ClientSecret", cancellationToken, logger);
        bool databaseConfigured = await HasSecretAsync(secrets, "ConnectionStrings:RequestPortal", cancellationToken, logger);
        bool allowedHostsConfigured = HasExplicitAllowedHosts(configuration["AllowedHosts"]);

        bool graphEmailEnabled = configuration.GetValue("Messaging:MicrosoftGraph:Email:Enabled", false);
        bool graphTeamsEnabled = configuration.GetValue("Messaging:MicrosoftGraph:Teams:Enabled", false);
        bool graphEmailTenantConfigured = HasValue(configuration, "Messaging:MicrosoftGraph:TenantId");
        bool graphEmailClientConfigured = HasValue(configuration, "Messaging:MicrosoftGraph:ClientId");
        bool graphEmailSecretConfigured = await HasSecretAsync(secrets, configuration["Messaging:MicrosoftGraph:ClientSecretName"] ?? "messaging/msgraph/client-secret", cancellationToken, logger);
        bool graphTeamsTokenConfigured = await HasSecretAsync(secrets, configuration["Messaging:MicrosoftGraph:Teams:DelegatedAccessTokenSecretName"] ?? "messaging/msgraph/teams/delegated-access-token", cancellationToken, logger);

        if (contract["requirements"] is JsonArray requirements)
        {
            foreach (JsonNode? node in requirements)
            {
                if (node is not JsonObject requirement) continue;
                string id = requirement["id"]?.GetValue<string>() ?? string.Empty;
                bool required = requirement["required"]?.GetValue<bool>() ?? false;
                (bool configured, string state, string? verification) = id switch
                {
                    "database-connection" => State(databaseConfigured, required, "RP-CONFIG-DB-001: PostgreSQL connection string must resolve through Common.Secrets."),
                    "storage-root" => State(HasDurablePath(configuration["Storage:Local:RootPath"]), required, "RP-PROD-STORAGE-001: Production document storage must use a durable path outside App_Data."),
                    "data-protection-keys" => State(HasDurablePath(configuration["Security:DataProtectionKeysPath"]), required, "RP-PROD-DP-001: Data Protection keys must be persisted outside the application deployment directory."),
                    "allowed-hosts" => State(allowedHostsConfigured, required, "RP-PROD-HOSTS-001: AllowedHosts must contain explicit production host names and may not be '*'."),
                    "staff-oidc-enabled" or "staff-oidc-authority" or "staff-oidc-client-id" or "staff-oidc-client-secret" => State(oidcConfigured, required, "RP-PROD-OIDC-001: Staff OIDC must be enabled with Authority, ClientId and a Common.Secrets-backed ClientSecret."),
                    "staff-reviewer-groups" or "staff-administrator-groups" => State(staffGroupsConfigured, required, "RP-PROD-STAFF-GROUPS-001: Reviewer and administrator group mappings are required."),
                    "storage-malware-scanner" => State(malwareConfigured, required, "RP-PROD-UPLOAD-001: Malware scanning must be enabled and have a scanner host."),
                    "external-representative-provider" => State(hasExternalProvider, required, "RP-PROD-OAUTH-001: At least one external representative identity provider must be commissioned."),
                    "smtp-host" or "smtp-from" or "notifications-enabled" => State(smtpConfigured, required, "RP-PROD-SMTP-001: Notifications must be enabled with SMTP host and From address."),
                    "smtp-password" => State(await HasSecretAsync(secrets, "Notifications:SmtpPassword", cancellationToken, logger), required, "RP-PROD-SMTP-SECRET-001: SMTP credential must be available through Common.Secrets when required by the SMTP server."),
                    "graph-tenant" => OptionalDependencyState(graphEmailEnabled, graphEmailTenantConfigured, "RP-MSG-GRAPH-EMAIL-001: Graph Email requires Messaging:MicrosoftGraph:TenantId."),
                    "graph-client" => OptionalDependencyState(graphEmailEnabled, graphEmailClientConfigured, "RP-MSG-GRAPH-EMAIL-002: Graph Email requires Messaging:MicrosoftGraph:ClientId."),
                    "graph-client-secret" => OptionalDependencyState(graphEmailEnabled, graphEmailSecretConfigured, "RP-MSG-GRAPH-EMAIL-003: Graph Email requires its client secret through Common.Secrets."),
                    "graph-email-enabled" => OptionalFeatureState(graphEmailEnabled),
                    "graph-email-sender" => OptionalDependencyState(graphEmailEnabled, HasValue(configuration, "Messaging:MicrosoftGraph:Email:SenderUpn"), "RP-MSG-GRAPH-EMAIL-004: Graph Email requires a sender UPN when enabled."),
                    "graph-teams-enabled" => OptionalFeatureState(graphTeamsEnabled),
                    "graph-teams-sender" => OptionalDependencyState(graphTeamsEnabled, HasValue(configuration, "Messaging:MicrosoftGraph:Teams:SenderUpn"), "RP-MSG-GRAPH-TEAMS-001: Graph Teams requires a sender UPN when enabled."),
                    "graph-teams-token" => OptionalDependencyState(graphTeamsEnabled, graphTeamsTokenConfigured, "RP-MSG-GRAPH-TEAMS-002: Graph Teams requires its delegated token through Common.Secrets."),
                    "messaging-queue" => State(HasValue(configuration, "Messaging:DurableQueuePath"), required, "RP-PROD-MESSAGING-001: Durable messaging queue storage must be configured."),
                    "site-id" => State(!string.IsNullOrWhiteSpace(siteId), required, "RP-PROD-SITE-001: Site:Id should identify this deployment."),
                    "public-url" => State(HasValue(configuration, "Aegis:PublicUrl"), required, "RP-PROD-PUBLIC-URL-001: Aegis:PublicUrl must identify the externally reachable Request Portal endpoint."),
                    _ => (false, "Unknown", "No runtime commissioning evaluator is defined for this requirement.")
                };
                requirement["isConfigured"] = configured;
                requirement["configurationState"] = state;
                requirement["effectiveValueAvailable"] = configured;
                requirement["verificationStatus"] = state == "Disabled" ? "NOT REQUIRED" : configured ? "PASS" : verification;
                foreach (string key in new[] { "displayValue", "safeDisplayValue", "defaultValue", "value", "currentValue", "resolvedValue", "effectiveValue", "example" }) requirement.Remove(key);
                if (id == "public-url" && configured) requirement["displayValue"] = configuration["Aegis:PublicUrl"]?.Trim();
            }
        }
        return contract;
    }

    private static ILifecycleEventSink CreateLifecycleSink(
        string operationsUrl,
        HttpClient client,
        string identityFile,
        string instanceId)
    {
        if (!Uri.TryCreate(
                operationsUrl.TrimEnd('/') + "/api/operations/activity/observe",
                UriKind.Absolute,
                out Uri? endpoint))
            return new NullLifecycleEventSink();

        var store = new FileRegistrationIdentityStore(identityFile);
        return new HttpLifecycleEventSink(
            client,
            new HttpLifecycleEventSinkOptions(endpoint, TimeSpan.FromSeconds(10)),
            async (request, lifecycleEvent, cancellationToken) =>
            {
                RegistrationIdentityDocument identity =
                    await store.LoadOrCreateAsync(
                        "RequestPortal",
                        instanceId,
                        cancellationToken);

                if (string.IsNullOrWhiteSpace(identity.Credential))
                    return;

                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer",
                        identity.Credential);
                request.Headers.TryAddWithoutValidation(
                    "X-Aegis-Application-Id",
                    identity.ApplicationId);
                request.Headers.TryAddWithoutValidation(
                    "X-Aegis-Instance-Id",
                    identity.InstanceId);
                request.Headers.TryAddWithoutValidation(
                    "X-Aegis-Installation-Id",
                    identity.InstallationId);
            });
    }

    private static (bool Configured, string State, string? Verification) State(bool configured, bool required, string message)
        => configured ? (true, "Configured", null) : required ? (false, "Missing", message) : (false, "Disabled", null);

    private static (bool Configured, string State, string? Verification) OptionalFeatureState(bool enabled)
        => enabled ? (true, "Configured", null) : (false, "Disabled", null);

    private static (bool Configured, string State, string? Verification) OptionalDependencyState(bool enabled, bool configured, string message)
        => !enabled ? (false, "Disabled", null) : configured ? (true, "Configured", null) : (false, "Missing", message);

    private static bool HasExplicitAllowedHosts(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] hosts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return hosts.Length > 0 && hosts.All(host => host != "*");
    }

    private static bool HasValue(IConfiguration configuration, string key) => !string.IsNullOrWhiteSpace(configuration[key]);
    private static bool HasSectionValues(IConfiguration configuration, string key) => configuration.GetSection(key).GetChildren().Any(child => !string.IsNullOrWhiteSpace(child.Value));
    private static async Task<bool> HasSecretAsync(ISecretProvider secrets, string name, CancellationToken cancellationToken, ILogger<ConfigurationContractPublisher>? logger)
    {
        try { return !string.IsNullOrWhiteSpace(await secrets.GetAsync(name, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger?.LogDebug(exception, "Unable to confirm whether Common.Secrets can resolve {SecretName}.", name); return false; }
    }
    private static bool HasDurablePath(string? value) => !string.IsNullOrWhiteSpace(value) && !value.Contains("App_Data", StringComparison.OrdinalIgnoreCase);
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
