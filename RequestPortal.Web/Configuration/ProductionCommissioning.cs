using Common.Secrets;
using Microsoft.Extensions.Configuration;

namespace RequestPortal.Web.Configuration;

internal static class ProductionCommissioning
{
    public static async Task<RuntimeSecrets> LoadRuntimeSecretsAsync(
        ISecretProvider provider,
        IConfiguration configuration)
    {
        string integrationSharedSecretName =
            configuration["FfpManagerIntegration:SharedSecretName"]?.Trim()
            ?? "FfpManagerIntegration:SharedSecret";

        string storageSecretName =
            configuration["Storage:SharePoint:ClientSecretName"]?.Trim()
            ?? "storage/sharepoint/client-secret";

        return new RuntimeSecrets(
            await provider.GetAsync("ConnectionStrings:RequestPortal"),
            await provider.GetAsync("Authentication:Google:ClientSecret"),
            await provider.GetAsync("Authentication:Microsoft:ClientSecret"),
            await provider.GetAsync("Authentication:Facebook:AppSecret"),
            await provider.GetAsync("Authentication:Yahoo:ClientSecret"),
            await provider.GetAsync("Authentication:Apple:ClientSecret"),
            await provider.GetAsync("Authentication:StaffOidc:ClientSecret"),
            await provider.GetAsync(integrationSharedSecretName),
            await provider.GetAsync(storageSecretName));
    }

    public static IReadOnlyList<ProductionConfigurationFailure> Validate(
        IConfiguration configuration,
        RuntimeSecrets secrets)
    {
        var failures = new List<ProductionConfigurationFailure>();
        string connectionString = secrets.ConnectionString ?? string.Empty;
        string storageProvider = configuration["Storage:Provider"]?.Trim() ?? "LocalFolder";
        string storageRoot = storageProvider.Equals("NetworkFolder", StringComparison.OrdinalIgnoreCase)
            ? configuration["Storage:NetworkFolder:RootPath"] ?? string.Empty
            : configuration["Storage:Local:RootPath"] ?? string.Empty;
        bool sharePointStorage = storageProvider.Equals("SharePoint", StringComparison.OrdinalIgnoreCase);
        string keyPath = configuration["Security:DataProtectionKeysPath"] ?? string.Empty;
        string allowedHosts = configuration["AllowedHosts"] ?? string.Empty;
        bool malwareScanningEnabled = configuration.GetValue("Uploads:MalwareScanning:Enabled", false);
        bool notificationsEnabled = configuration.GetValue("Notifications:Enabled", false);
        bool integrationEnabled = configuration.GetValue("FfpManagerIntegration:Enabled", false);
        string[] reviewerGroups = configuration.GetSection("Authentication:ActiveDirectory:ReviewerGroups").Get<string[]>() ?? Array.Empty<string>();
        string[] administratorGroups = configuration.GetSection("Authentication:ActiveDirectory:AdministratorGroups").Get<string[]>() ?? Array.Empty<string>();
        string? googleClientId = configuration["Authentication:Google:ClientId"];
        string? googleClientSecret = secrets.GoogleClientSecret;
        string? microsoftClientId = configuration["Authentication:Microsoft:ClientId"];
        string? microsoftClientSecret = secrets.MicrosoftClientSecret;
        string? facebookAppId = configuration["Authentication:Facebook:AppId"];
        string? facebookAppSecret = secrets.FacebookAppSecret;
        string? yahooClientId = configuration["Authentication:Yahoo:ClientId"];
        string? yahooClientSecret = secrets.YahooClientSecret;
        string? appleClientId = configuration["Authentication:Apple:ClientId"];
        string? appleClientSecret = secrets.AppleClientSecret;
        string? staffOidcClientSecret = secrets.StaffOidcClientSecret;
        string? integrationSharedSecret = secrets.FfpManagerSharedSecret;
        bool hasExternalProvider =
            (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret)) ||
            (!string.IsNullOrWhiteSpace(microsoftClientId) && !string.IsNullOrWhiteSpace(microsoftClientSecret)) ||
            (!string.IsNullOrWhiteSpace(facebookAppId) && !string.IsNullOrWhiteSpace(facebookAppSecret)) ||
            (!string.IsNullOrWhiteSpace(yahooClientId) && !string.IsNullOrWhiteSpace(yahooClientSecret)) ||
            (!string.IsNullOrWhiteSpace(appleClientId) && !string.IsNullOrWhiteSpace(appleClientSecret));
        bool staffOidcEnabled = configuration.GetValue("Authentication:StaffOidc:Enabled", false);

        AddIf(string.IsNullOrWhiteSpace(connectionString),
            "RP-CONFIG-DB-001",
            "PostgreSQL connection string 'RequestPortal' is not configured through Common.Secrets.");
        AddIf(!sharePointStorage && (string.IsNullOrWhiteSpace(storageRoot) || storageRoot.Contains("App_Data", StringComparison.OrdinalIgnoreCase)),
            "RP-PROD-STORAGE-001",
            "Production document storage must use SharePoint or a durable external filesystem/network target.");
        AddIf(sharePointStorage &&
              (string.IsNullOrWhiteSpace(configuration["Storage:SharePoint:TenantId"]) ||
               string.IsNullOrWhiteSpace(configuration["Storage:SharePoint:ClientId"]) ||
               string.IsNullOrWhiteSpace(configuration["Storage:SharePoint:HostName"]) ||
               string.IsNullOrWhiteSpace(secrets.StorageSharePointClientSecret)),
            "RP-PROD-STORAGE-SP-001",
            "SharePoint storage requires TenantId, ClientId, HostName and a Common.Secrets-backed client secret.");
        AddIf(string.IsNullOrWhiteSpace(keyPath) || keyPath.Contains("App_Data", StringComparison.OrdinalIgnoreCase),
            "RP-PROD-DP-001",
            "Production Data Protection keys must be persisted outside the application deployment directory.");
        AddIf(allowedHosts == "*" || string.IsNullOrWhiteSpace(allowedHosts),
            "RP-PROD-HOSTS-001",
            "AllowedHosts must be restricted in production.");
        AddIf(!malwareScanningEnabled || string.IsNullOrWhiteSpace(configuration["Uploads:MalwareScanning:Host"]),
            "RP-PROD-UPLOAD-001",
            "Malware scanning must be configured and enabled in production.");
        AddIf(reviewerGroups.Length == 0 || administratorGroups.Length == 0,
            "RP-PROD-STAFF-GROUPS-001",
            "FFP staff directory-group mappings must be configured for Common.Security.");
        AddIf(!staffOidcEnabled ||
              string.IsNullOrWhiteSpace(configuration["Authentication:StaffOidc:Authority"]) ||
              string.IsNullOrWhiteSpace(configuration["Authentication:StaffOidc:ClientId"]) ||
              string.IsNullOrWhiteSpace(staffOidcClientSecret),
            "RP-PROD-OIDC-001",
            "FFP staff OIDC Authority, ClientId and Common.Secrets-backed ClientSecret must be commissioned.");
        AddIf(!hasExternalProvider,
            "RP-PROD-OAUTH-001",
            "At least one external representative identity provider must be commissioned.");
        AddIf(!notificationsEnabled ||
              string.IsNullOrWhiteSpace(configuration["Notifications:SmtpHost"]) ||
              string.IsNullOrWhiteSpace(configuration["Notifications:FromAddress"]),
            "RP-PROD-SMTP-001",
            "SMTP notifications must be commissioned for production.");
        AddIf(string.IsNullOrWhiteSpace(configuration["Messaging:DurableQueuePath"]),
            "RP-PROD-MESSAGING-001",
            "Common.Messaging durable queue path must be configured in production.");
        AddIf(string.IsNullOrWhiteSpace(configuration["Site:Id"]),
            "RP-PROD-SITE-001",
            "Site:Id must identify the production deployment.");

        string? integrationBaseUrl = configuration["FfpManagerIntegration:BaseUrl"];
        bool integrationHttps =
            Uri.TryCreate(integrationBaseUrl, UriKind.Absolute, out Uri? integrationUri) &&
            integrationUri.Scheme == Uri.UriSchemeHttps;
        AddIf(integrationEnabled &&
              (!integrationHttps || string.IsNullOrWhiteSpace(integrationSharedSecret)),
            "RP-PROD-FFPMANAGER-001",
            "FFP Manager integration requires an HTTPS BaseUrl and a Common.Secrets-backed SharedSecret when enabled.");

        return failures;

        void AddIf(bool condition, string code, string message)
        {
            if (condition)
            {
                failures.Add(new ProductionConfigurationFailure(code, message));
            }
        }
    }

    public static string BuildFailureMessage(
        IReadOnlyList<ProductionConfigurationFailure> failures)
    {
        return "RequestPortal production commissioning failed with " +
            failures.Count +
            " issue(s):\n" +
            string.Join("\n", failures.Select(
                failure => $"{failure.Code}: {failure.Message}"));
    }
}

internal sealed record RuntimeSecrets(
    string? ConnectionString,
    string? GoogleClientSecret,
    string? MicrosoftClientSecret,
    string? FacebookAppSecret,
    string? YahooClientSecret,
    string? AppleClientSecret,
    string? StaffOidcClientSecret,
    string? FfpManagerSharedSecret,
    string? StorageSharePointClientSecret);

internal sealed record ProductionConfigurationFailure(
    string Code,
    string Message);
