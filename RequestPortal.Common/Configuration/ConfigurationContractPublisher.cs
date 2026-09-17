using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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
            if (await PublishBootstrapAsync(configuration, environment, secrets, stoppingToken, logger))
            {
                return;
            }

            try
            {
                await Task.Delay(retryDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Publishes the RequestPortal commissioning contract before the web host is allowed to fail
    /// production validation. This is intentionally usable during bootstrap so Aegis.Configuration
    /// can show incomplete commissioning state even when RequestPortal cannot fully start.
    /// </summary>
    public static async Task<bool> PublishBootstrapAsync(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ISecretProvider secrets,
        CancellationToken cancellationToken = default,
        ILogger<ConfigurationContractPublisher>? logger = null)
    {
        string? baseUrl = configuration["Aegis:Configuration:Url"]
            ?? configuration["AegisConfiguration:BaseUrl"]
            ?? configuration["ConfigurationService:BaseUrl"];

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger?.LogInformation("Aegis.Configuration publication is disabled because no Configuration URL is configured.");
            return false;
        }

        string path = configuration["Aegis:Configuration:ContractPath"]
            ?? configuration["AegisConfiguration:ContractPath"]
            ?? configuration["ConfigurationService:ContractPath"]
            ?? Path.Combine(environment.ContentRootPath, "configuration", "RequestPortal.configuration-contract.json");

        try
        {
            if (!File.Exists(path))
            {
                logger?.LogWarning("Configuration contract was not found at {ContractPath}; RequestPortal will retry publication.", path);
                return false;
            }

            RegistrationCredentialResolution credential = await RegistrationCredentialResolver.ResolveAsync(
                () => Environment.GetEnvironmentVariable(RegistrationCredentialResolver.EnvironmentVariableName),
                ct => secrets.GetAsync(RegistrationCredentialResolver.SecretNameFor("RequestPortal"), ct),
                () => configuration[RegistrationCredentialResolver.AppSettingKey],
                cancellationToken);

            if (!credential.Succeeded)
            {
                logger?.LogWarning(
                    "Aegis.Configuration registration credential is unavailable from Environment, SecretProvider, and AppSetting; RequestPortal will retry publication. Secret provider detail: {SecretProviderError}",
                    credential.SecretProviderError ?? "none");
                return false;
            }

            JsonObject contract = await BuildRuntimeContractAsync(configuration, environment, secrets, path, cancellationToken, logger);
            using HttpClient client = new();
            var registrar = new ConfigurationRegistrar(
                client,
                new RegistrationOptions(
                    new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute),
                    credential.Credential,
                    TimeSpan.FromSeconds(10)));

            RegistrationResult result = await registrar.RegisterContractAsync(contract, cancellationToken);
            if (!result.Succeeded)
            {
                logger?.LogWarning("Aegis.Configuration registration failed for RequestPortal: {RegistrationError}; RequestPortal will retry publication.", result.Error ?? "Unknown registration failure.");
                return false;
            }

            logger?.LogInformation(
                "RequestPortal commissioning state registered with Aegis.Configuration with HTTP {StatusCode} using {CredentialSource} credential.",
                (int?)result.StatusCode,
                credential.Source);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Unable to publish RequestPortal commissioning state; RequestPortal will retry publication.");
            return false;
        }
    }

    private static async Task<JsonObject> BuildRuntimeContractAsync(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ISecretProvider secrets,
        string path,
        CancellationToken cancellationToken,
        ILogger<ConfigurationContractPublisher>? logger)
    {
        JsonObject contract = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("The RequestPortal configuration contract is not a JSON object.");

        contract["version"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? contract["version"]?.GetValue<string>()
            ?? "unknown";
        string? siteId = configuration["Site:Id"];
        contract["siteId"] = NullIfWhiteSpace(siteId);
        contract["instanceId"] = NullIfWhiteSpace(configuration["Service:Identity"] ?? Environment.MachineName);

        bool hasExternalProvider =
            (HasValue(configuration, "Authentication:Google:ClientId") && await HasSecretOrValueAsync(configuration, secrets, "Authentication:Google:ClientSecret", cancellationToken, logger)) ||
            (HasValue(configuration, "Authentication:Microsoft:ClientId") && await HasSecretOrValueAsync(configuration, secrets, "Authentication:Microsoft:ClientSecret", cancellationToken, logger)) ||
            (HasValue(configuration, "Authentication:Facebook:AppId") && await HasSecretOrValueAsync(configuration, secrets, "Authentication:Facebook:AppSecret", cancellationToken, logger));

        bool staffGroupsConfigured = HasSectionValues(configuration, "Authentication:ActiveDirectory:ReviewerGroups")
            && HasSectionValues(configuration, "Authentication:ActiveDirectory:AdministratorGroups");
        bool malwareConfigured = configuration.GetValue("Uploads:MalwareScanning:Enabled", false)
            && HasValue(configuration, "Uploads:MalwareScanning:Host");
        bool smtpConfigured = configuration.GetValue("Notifications:Enabled", false)
            && HasValue(configuration, "Notifications:SmtpHost")
            && HasValue(configuration, "Notifications:FromAddress");
        bool oidcConfigured = configuration.GetValue("Authentication:StaffOidc:Enabled", false)
            && HasValue(configuration, "Authentication:StaffOidc:Authority")
            && HasValue(configuration, "Authentication:StaffOidc:ClientId")
            && await HasSecretOrValueAsync(configuration, secrets, "Authentication:StaffOidc:ClientSecret", cancellationToken, logger);

        bool graphEmailEnabled = configuration.GetValue("Messaging:MicrosoftGraph:Email:Enabled", false);
        string graphClientSecretName = configuration["Messaging:MicrosoftGraph:ClientSecretName"]
            ?? "messaging/msgraph/client-secret";
        bool graphEmailConfigured = !graphEmailEnabled ||
            (HasValue(configuration, "Messaging:MicrosoftGraph:TenantId")
             && HasValue(configuration, "Messaging:MicrosoftGraph:ClientId")
             && HasValue(configuration, "Messaging:MicrosoftGraph:Email:SenderUpn")
             && await HasSecretOrValueAsync(configuration, secrets, graphClientSecretName, cancellationToken, logger));

        bool graphTeamsEnabled = configuration.GetValue("Messaging:MicrosoftGraph:Teams:Enabled", false);
        string graphTeamsTokenName = configuration["Messaging:MicrosoftGraph:Teams:DelegatedAccessTokenSecretName"]
            ?? "messaging/msgraph/teams/delegated-access-token";
        bool graphTeamsConfigured = !graphTeamsEnabled ||
            (HasValue(configuration, "Messaging:MicrosoftGraph:Teams:SenderUpn")
             && await HasSecretOrValueAsync(configuration, secrets, graphTeamsTokenName, cancellationToken, logger));

        if (contract["requirements"] is JsonArray requirements)
        {
            foreach (JsonNode? node in requirements)
            {
                if (node is not JsonObject requirement)
                {
                    continue;
                }

                string id = requirement["id"]?.GetValue<string>() ?? string.Empty;
                (bool configured, string? verification) = id switch
                {
                    "database-connection" =>
                        (await HasSecretOrValueAsync(configuration, secrets, "ConnectionStrings:RequestPortal", cancellationToken, logger)
                            || HasNonPlaceholderValue(configuration.GetConnectionString("RequestPortal")),
                         "RP-CONFIG-DB-001: PostgreSQL connection string must resolve through Common.Secrets/OpenBao or deployment configuration."),
                    "storage-root" =>
                        (HasDurablePath(configuration["Storage:Local:RootPath"]),
                         "RP-PROD-STORAGE-001: Production document storage must use a durable path outside App_Data."),
                    "data-protection-keys" =>
                        (HasDurablePath(configuration["Security:DataProtectionKeysPath"]),
                         "RP-PROD-DP-001: Data Protection keys must be persisted outside the application deployment directory."),
                    "openbao-address" =>
                        (HasAnyValue(configuration, "CommonSecrets:OpenBao:Address", "OpenBao:Address", "OPENBAO_ADDR", "VAULT_ADDR"),
                         "RP-PROD-SECRETS-001: Common.Secrets OpenBao address is required."),
                    "openbao-role" =>
                        (HasAnyValue(configuration, "CommonSecrets:OpenBao:RoleId", "OpenBao:RoleId", "OPENBAO_ROLE_ID")
                            || await HasSecretOrValueAsync(configuration, secrets, "CommonSecrets:OpenBao:RoleId", cancellationToken, logger),
                         "RP-PROD-SECRETS-002: OpenBao AppRole RoleId is required."),
                    "allowed-hosts" =>
                        (HasRestrictedHosts(configuration),
                         "RP-PROD-HOSTS-001: AllowedHosts must be restricted in production."),
                    "staff-oidc-enabled" or "staff-oidc-authority" or "staff-oidc-client-id" or "staff-oidc-client-secret" =>
                        (oidcConfigured,
                         "RP-PROD-OIDC-001: Staff OIDC must be enabled with Authority, ClientId and OpenBao-backed ClientSecret."),
                    "staff-reviewer-groups" or "staff-administrator-groups" =>
                        (staffGroupsConfigured,
                         "RP-PROD-STAFF-GROUPS-001: Reviewer and administrator group mappings are required."),
                    "storage-malware-scanner" =>
                        (malwareConfigured,
                         "RP-PROD-UPLOAD-001: Malware scanning must be enabled and have a scanner host."),
                    "external-representative-provider" =>
                        (hasExternalProvider,
                         "RP-PROD-OAUTH-001: At least one external representative identity provider must be commissioned."),
                    "smtp-host" or "smtp-from" or "notifications-enabled" =>
                        (smtpConfigured,
                         "RP-PROD-SMTP-001: Notifications must be enabled with SMTP host and From address."),
                    "smtp-password" =>
                        (await HasSecretOrValueAsync(configuration, secrets, "Notifications:SmtpPassword", cancellationToken, logger),
                         "RP-PROD-SMTP-SECRET-001: SMTP credential must be available through Common.Secrets/OpenBao when required by the SMTP server."),
                    "graph-email-enabled" =>
                        (true, null),
                    "graph-tenant" or "graph-client" or "graph-client-secret" or "graph-email-sender" =>
                        (graphEmailConfigured,
                         "RP-MSG-GRAPH-EMAIL-001: When Graph Email is enabled, TenantId, ClientId, sender UPN and the Common.Secrets client credential must all be commissioned."),
                    "graph-teams-enabled" =>
                        (true, null),
                    "graph-teams-sender" or "graph-teams-token" =>
                        (graphTeamsConfigured,
                         "RP-MSG-GRAPH-TEAMS-001: When Graph Teams is enabled, sender UPN and the Common.Secrets delegated token must both be commissioned."),
                    "messaging-queue" =>
                        (HasValue(configuration, "Messaging:DurableQueuePath"),
                         "RP-PROD-MESSAGING-001: Common.Messaging durable queue path is required."),
                    "site-id" =>
                        (!string.IsNullOrWhiteSpace(siteId),
                         "RP-PROD-SITE-001: Site:Id should identify this deployment."),
                    _ => (false, null)
                };

                requirement["isConfigured"] = configured;
                requirement["configurationState"] = configured ? "Configured" : "Missing";
                requirement["effectiveValueAvailable"] = configured;
                requirement["verificationStatus"] = configured
                    ? "PASS"
                    : verification;
                requirement.Remove("safeDisplayValue");
                requirement.Remove("defaultValue");
                requirement.Remove("value");
                requirement.Remove("currentValue");
                requirement.Remove("resolvedValue");
                requirement.Remove("effectiveValue");
                requirement.Remove("example");
            }
        }

        return contract;
    }

    private static bool HasValue(IConfiguration configuration, string key)
        => !string.IsNullOrWhiteSpace(configuration[key]);

    private static bool HasSectionValues(IConfiguration configuration, string key)
        => configuration.GetSection(key).GetChildren().Any(child => !string.IsNullOrWhiteSpace(child.Value));

    private static bool HasAnyValue(IConfiguration configuration, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (HasValue(configuration, key) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> HasSecretOrValueAsync(
        IConfiguration configuration,
        ISecretProvider secrets,
        string name,
        CancellationToken cancellationToken,
        ILogger<ConfigurationContractPublisher>? logger)
    {
        if (HasNonPlaceholderValue(configuration[name]))
        {
            return true;
        }

        try
        {
            return !string.IsNullOrWhiteSpace(await secrets.GetAsync(name, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogDebug(exception, "Unable to confirm whether configuration secret {SecretName} is available.", name);
            return false;
        }
    }

    private static bool HasRestrictedHosts(IConfiguration configuration)
    {
        string? hosts = configuration["AllowedHosts"];
        return !string.IsNullOrWhiteSpace(hosts) && hosts.Trim() != "*";
    }

    private static bool HasDurablePath(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.Contains("App_Data", StringComparison.OrdinalIgnoreCase);

    private static bool HasNonPlaceholderValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
