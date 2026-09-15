using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Common.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RequestPortal.Common.Configuration;

public sealed class ConfigurationContractPublisher(IConfiguration configuration, IWebHostEnvironment environment, ISecretProvider secrets, ILogger<ConfigurationContractPublisher> logger) : BackgroundService
{
    private const string ApplicationId = "RequestPortal";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? baseUrl = First(configuration["Aegis:Configuration:Url"], configuration["AegisConfiguration:BaseUrl"], configuration["ConfigurationService:BaseUrl"]);
        if (string.IsNullOrWhiteSpace(baseUrl)) { logger.LogInformation("Aegis.Configuration publication is disabled because no Configuration URL is configured."); return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await TryPublishAsync(baseUrl, stoppingToken)) return;
            try { await Task.Delay(RetryDelay, stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    private async Task<bool> TryPublishAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            string? registrationKey = await secrets.GetAsync($"configuration/registration/{ApplicationId}", ct);
            if (string.IsNullOrWhiteSpace(registrationKey)) { logger.LogWarning("Configuration registration credential is unavailable; RequestPortal remains operational."); return false; }
            string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            string? siteId = First(configuration["Site:Id"], configuration["Deployment:SiteId"]);
            string instanceId = First(configuration["Service:Identity"], Environment.MachineName) ?? Environment.MachineName;

            bool databaseConfigured = await SecretExistsAsync("ConnectionStrings:RequestPortal", ct) || HasNonPlaceholderValue(configuration.GetConnectionString("RequestPortal"));
            bool oidcEnabled = configuration.GetValue("Authentication:StaffOidc:Enabled", false);
            bool oidcSecret = await SecretExistsAsync("Authentication:StaffOidc:ClientSecret", ct) || HasValue(configuration["Authentication:StaffOidc:ClientSecret"]);
            bool smtpEnabled = configuration.GetValue("Notifications:Enabled", false);
            string smtpPasswordName = configuration["Notifications:PasswordSecretName"] ?? "Notifications:SmtpPassword";
            bool smtpPassword = await SecretExistsAsync(smtpPasswordName, ct);

            List<object> requirements =
            [
                Config("configuration-url", "Aegis:Configuration:Url", "System Configuration URL", false, "Aegis.Configuration endpoint."),
                Config("diagnostics-url", "Aegis:Diagnostics:Url", "System Diagnostics URL", false, "Aegis.Diagnostics endpoint."),
                Secret("database", "ConnectionStrings:RequestPortal", "RequestPortal Database Connection", true, "PostgreSQL connection string used by RequestPortal.", databaseConfigured, "ConnectionStrings:RequestPortal"),
                Config("allowed-hosts", "AllowedHosts", "Allowed Hosts", true, "ASP.NET Core allowed host names."),
                Config("demo-enabled", "Demo:Enabled", "Demo Mode", false, "Enables development/demo behavior.", "false"),
                Config("demo-database", "Demo:EnsureDatabase", "Demo Ensure Database", false, "Allows demo database initialization.", "false", "Demo:Enabled=true"),
                Config("dashboard-new", "ManagementDashboard:NewRequestHours", "New Request Hours", false, "Management dashboard threshold for new requests.", "48"),
                Config("dashboard-overdue", "ManagementDashboard:OverdueDays", "Overdue Days", false, "Management dashboard overdue threshold.", "14"),
                Config("dashboard-stalled", "ManagementDashboard:StalledDays", "Stalled Days", false, "Management dashboard stalled threshold.", "7"),
                Config("dashboard-trend", "ManagementDashboard:TrendWeeks", "Trend Weeks", false, "Management dashboard trend window.", "12"),
                Config("ffp-enabled", "FfpManagerIntegration:Enabled", "FFP Manager Integration Enabled", false, "Enables RequestPortal-to-FFP Manager integration.", "false"),
                Config("ffp-url", "FfpManagerIntegration:BaseUrl", "FFP Manager Bridge URL", false, "FFP Manager bridge endpoint.", null, "FfpManagerIntegration:Enabled=true"),
                Secret("ffp-shared-secret", "FfpManagerIntegration:SharedSecret", "FFP Manager Bridge Shared Secret", configuration.GetValue("FfpManagerIntegration:Enabled", false), "Shared authentication secret for the bridge.", HasValue(configuration["FfpManagerIntegration:SharedSecret"]), "FfpManagerIntegration:SharedSecret", "FfpManagerIntegration:Enabled=true"),
                Config("ffp-timeout", "FfpManagerIntegration:TimeoutSeconds", "FFP Manager Timeout", false, "Bridge request timeout.", "30", "FfpManagerIntegration:Enabled=true"),
                Config("ffp-poll", "FfpManagerIntegration:PollIntervalSeconds", "FFP Manager Poll Interval", false, "Bridge status polling interval.", "60", "FfpManagerIntegration:Enabled=true"),
                Config("ffp-system", "FfpManagerIntegration:SystemName", "FFP Manager System Name", false, "External system label.", "FFPManager", "FfpManagerIntegration:Enabled=true"),
                Config("storage-root", "Storage:Local:RootPath", "Document Storage Root", true, "Root filesystem/share path for uploaded documents.", "App_Data/storage"),
                Config("data-protection", "Security:DataProtectionKeysPath", "Data Protection Keys Path", true, "Persistent ASP.NET Data Protection key store.", "App_Data/keys"),
                Config("queue-path", "Messaging:DurableQueuePath", "Messaging Queue Path", false, "Filesystem queue path used by messaging when configured."),
                Config("messaging-provider", "CommonMessaging:Durability:Provider", "Messaging Durability Provider", false, "Common.Messaging durability engine."),
                Config("messaging-connection", "CommonMessaging:Durability:ConnectionStringName", "Messaging Connection Name", false, "Named database connection used by durable messaging.", "RequestPortal"),
                Config("messaging-queue", "CommonMessaging:Durability:QueueName", "Messaging Queue Name", false, "Durable message queue name.", "requestportal"),
                Config("upload-max", "Uploads:MaxBytes", "Maximum Upload Bytes", false, "Maximum request-document upload size.", "26214400"),
                Config("clamav-enabled", "Uploads:MalwareScanning:Enabled", "Malware Scanning Enabled", false, "Enables ClamAV scanning.", "false"),
                Config("clamav-host", "Uploads:MalwareScanning:Host", "ClamAV Host", false, "ClamAV daemon host.", null, "Uploads:MalwareScanning:Enabled=true"),
                Config("clamav-port", "Uploads:MalwareScanning:Port", "ClamAV Port", false, "ClamAV daemon TCP port.", "3310", "Uploads:MalwareScanning:Enabled=true"),
                Config("notifications-enabled", "Notifications:Enabled", "Notifications Enabled", false, "Enables email notification delivery.", "false"),
                Config("smtp-host", "Notifications:SmtpHost", "SMTP Host", false, "SMTP server host.", null, "Notifications:Enabled=true"),
                Config("smtp-port", "Notifications:SmtpPort", "SMTP Port", false, "SMTP server port.", "587", "Notifications:Enabled=true"),
                Config("smtp-ssl", "Notifications:EnableSsl", "SMTP SSL", false, "Enable SMTP TLS/SSL.", "true", "Notifications:Enabled=true"),
                Config("smtp-from", "Notifications:FromAddress", "SMTP From Address", false, "Notification sender address.", null, "Notifications:Enabled=true"),
                Config("smtp-from-name", "Notifications:FromName", "SMTP From Name", false, "Notification sender display name.", "FFP Request Portal", "Notifications:Enabled=true"),
                Config("smtp-user", "Notifications:UserName", "SMTP Username", false, "SMTP account name; not treated as a password."),
                Config("smtp-password-name", "Notifications:PasswordSecretName", "SMTP Password Secret Name", false, "Logical secret name for the SMTP password.", "Notifications:SmtpPassword"),
                Secret("smtp-password", smtpPasswordName, "SMTP Password", smtpEnabled, "SMTP authentication password.", smtpPassword, smtpPasswordName, "Notifications:Enabled=true"),
                Config("sms-enabled", "Notifications:SmsEnabled", "SMS Enabled", false, "Enables SMS delivery.", "false"),
                Config("sms-endpoint", "Notifications:SmsEndpoint", "SMS Endpoint", false, "SMS service endpoint.", null, "Notifications:SmsEnabled=true"),
                Config("whatsapp-enabled", "Notifications:WhatsAppEnabled", "WhatsApp Enabled", false, "Enables WhatsApp delivery.", "false"),
                Config("whatsapp-endpoint", "Notifications:WhatsAppEndpoint", "WhatsApp Endpoint", false, "WhatsApp service endpoint.", null, "Notifications:WhatsAppEnabled=true"),
                Config("oidc-enabled", "Authentication:StaffOidc:Enabled", "Staff OIDC Enabled", false, "Enables FFP staff OpenID Connect authentication.", "false"),
                Config("oidc-authority", "Authentication:StaffOidc:Authority", "Staff OIDC Authority", oidcEnabled, "OIDC identity-provider authority.", null, "Authentication:StaffOidc:Enabled=true"),
                Config("oidc-client", "Authentication:StaffOidc:ClientId", "Staff OIDC Client ID", oidcEnabled, "OIDC client identifier.", "request-portal", "Authentication:StaffOidc:Enabled=true"),
                Secret("oidc-secret", "Authentication:StaffOidc:ClientSecret", "Staff OIDC Client Secret", oidcEnabled, "OIDC confidential-client credential.", oidcSecret, "Authentication:StaffOidc:ClientSecret", "Authentication:StaffOidc:Enabled=true"),
                Config("oidc-callback", "Authentication:StaffOidc:CallbackPath", "Staff OIDC Callback Path", false, "OIDC callback path.", "/signin-ffp-staff", "Authentication:StaffOidc:Enabled=true"),
                Config("ad-domain", "Authentication:ActiveDirectory:Domain", "Staff Domain Name", false, "Domain label used for staff identity/group policy.", "fftpj.local"),
                Config("reviewer-groups", "Authentication:ActiveDirectory:ReviewerGroups", "Reviewer Groups", false, "Groups authorized as RequestPortal reviewers."),
                Config("administrator-groups", "Authentication:ActiveDirectory:AdministratorGroups", "Administrator Groups", false, "Groups authorized as RequestPortal administrators."),
                Config("openbao-address", "CommonSecrets:OpenBao:Address", "OpenBao Address", false, "OpenBao endpoint used by Common.Secrets."),
                Config("openbao-mount", "CommonSecrets:OpenBao:MountPath", "OpenBao Mount Path", false, "OpenBao KV mount.", "secret"),
                Config("openbao-base", "CommonSecrets:OpenBao:BasePath", "OpenBao Base Path", false, "RequestPortal OpenBao base path.", "requestportal"),
                Config("openbao-auth-mount", "CommonSecrets:OpenBao:AuthMountPath", "OpenBao Auth Mount", false, "OpenBao authentication mount.", "approle"),
                Config("openbao-role", "CommonSecrets:OpenBao:RoleId", "OpenBao Role ID", false, "OpenBao AppRole role identifier."),
                Config("openbao-secret-env", "CommonSecrets:OpenBao:SecretIdEnvironmentVariable", "Secret0 Environment Variable Name", false, "Environment variable name from which Secret0 may be consumed."),
                Config("openbao-secret-file", "CommonSecrets:OpenBao:SecretIdFile", "Secret0 File", false, "Secure file from which Secret0 may be consumed."),
                Secret("secret-zero", "Secret0", "OpenBao Secret Zero", environment.IsProduction(), "Bootstrap material used to authenticate to OpenBao; never published.", SecretZeroDetected(), "OpenBao bootstrap")
            ];

            AddExternalLogin(requirements, "Google", "ClientId", "ClientSecret", await SecretExistsAsync("Authentication:Google:ClientSecret", ct));
            AddExternalLogin(requirements, "Microsoft", "ClientId", "ClientSecret", await SecretExistsAsync("Authentication:Microsoft:ClientSecret", ct));
            AddExternalLogin(requirements, "Facebook", "AppId", "AppSecret", await SecretExistsAsync("Authentication:Facebook:AppSecret", ct));

            List<object> dependencies =
            [
                Dependency("database", "RequestPortal PostgreSQL", true, "Startup", "Stores portal identity, requests, approvals-facing state, auditing and messaging durability.", "RequestPortal cannot start without its database.", "Npgsql / Entity Framework Core", "PostgreSQL", null, null),
                Dependency("configuration", "Aegis.Configuration", false, "Feature", "Receives RequestPortal operational contracts.", "Portal remains operational without centralized contract visibility.", "HTTP/HTTPS JSON", "Aegis.Configuration", baseUrl, await ReachableHttpAsync(baseUrl, ct)),
                Dependency("openbao", "OpenBao", environment.IsProduction(), "Startup", "Provides production secrets through Common.Secrets.", "Production secret-backed features cannot initialize correctly.", "OpenBao HTTP API via Common.Secrets", "OpenBao", OpenBaoAddress(), await ReachableHttpAsync(OpenBaoAddress(), ct)),
                Dependency("storage", "RequestPortal Document Storage", true, "Feature", "Stores request documents and data-protection keys on configured filesystem/share paths.", "Request intake can run only to the extent file-backed operations remain available; document operations fail when storage is unavailable.", "Filesystem/SMB path", "Filesystem", configuration["Storage:Local:RootPath"], null)
            ];
            if (configuration.GetValue("FfpManagerIntegration:Enabled", false)) dependencies.Add(Dependency("ffpmanager", "FFP Manager Bridge", false, "Feature", "Synchronizes approved portal data/status with FFP Manager.", "Portal continues; FFP Manager synchronization is deferred/unavailable.", "HTTPS/JSON", "FfpManager.Bridge", configuration["FfpManagerIntegration:BaseUrl"], await ReachableHttpAsync(configuration["FfpManagerIntegration:BaseUrl"], ct)));
            if (configuration.GetValue("Uploads:MalwareScanning:Enabled", false)) dependencies.Add(Dependency("clamav", "ClamAV", true, "Feature", "Scans uploaded request documents for malware.", "Uploads requiring malware validation cannot be accepted safely.", "ClamAV TCP protocol", "ClamAV", HostPort("Uploads:MalwareScanning:Host", "Uploads:MalwareScanning:Port", 3310), await ReachableTcpAsync(configuration["Uploads:MalwareScanning:Host"], configuration.GetValue("Uploads:MalwareScanning:Port", 3310), ct)));
            if (smtpEnabled) dependencies.Add(Dependency("smtp", "SMTP Server", false, "Feature", "Sends RequestPortal notifications.", "Portal continues; email notifications queue/fail according to messaging policy.", "SMTP", "SMTP", HostPort("Notifications:SmtpHost", "Notifications:SmtpPort", 587), await ReachableTcpAsync(configuration["Notifications:SmtpHost"], configuration.GetValue("Notifications:SmtpPort", 587), ct)));
            if (oidcEnabled) dependencies.Add(Dependency("staff-oidc", "Staff OIDC Identity Provider", true, "Feature", "Authenticates FFP staff users.", "Public portal remains available but staff OIDC sign-in is unavailable.", "OpenID Connect", "OIDC Provider", configuration["Authentication:StaffOidc:Authority"], await ReachableHttpAsync(configuration["Authentication:StaffOidc:Authority"], ct)));

            object registration = new
            {
                configuration = new { applicationId = ApplicationId, displayName = "Request Portal", version, siteId, instanceId, description = "External/internal request intake, document, notification and FFP Manager integration portal.", requirements },
                dependencies = new { applicationId = ApplicationId, version, siteId, instanceId, dependencies },
                environment = new { applicationId = ApplicationId, version, siteId, instanceId, environment = await DetectEnvironmentAsync(ct) }
            };

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            using HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/registrations/register");
            request.Headers.TryAddWithoutValidation("X-Aegis-Registration-Key", registrationKey);
            request.Content = new StringContent(JsonSerializer.Serialize(registration), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) { logger.LogWarning("Aegis.Configuration rejected RequestPortal operational registration with HTTP {StatusCode}.", (int)response.StatusCode); return false; }
            logger.LogInformation("RequestPortal published configuration, dependency and detected-environment contracts.");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogWarning(exception, "Unable to publish RequestPortal operational contracts; RequestPortal remains operational."); return false; }
    }

    private void AddExternalLogin(List<object> requirements, string section, string idKey, string secretKey, bool secretPresent)
    {
        string prefix = $"Authentication:{section}";
        requirements.Add(Config($"{section.ToLowerInvariant()}-id", $"{prefix}:{idKey}", $"{section} Login ID", false, $"Client/application identifier for optional {section} public login."));
        requirements.Add(Secret($"{section.ToLowerInvariant()}-secret", $"{prefix}:{secretKey}", $"{section} Login Secret", false, $"Credential for optional {section} public login.", secretPresent || HasValue(configuration[$"{prefix}:{secretKey}"]), $"{prefix}:{secretKey}", $"{prefix}:{idKey} configured"));
    }

    private object Config(string id, string key, string name, bool required, string purpose, string? defaultValue = null, string? condition = null)
    {
        string envKey = key.Replace(":", "__", StringComparison.Ordinal); string? env = Environment.GetEnvironmentVariable(envKey); string? configured = configuration[key]; string? value = First(env, configured, defaultValue);
        return new { id, displayName = name, kind = "Configuration", required, purpose, configurationKey = key, isConfigured = HasValue(value), safeDisplayValue = value, sensitive = false, conditionalOn = condition, allowedSources = new[] { "Environment Variable", $"appsettings.{environment.EnvironmentName}.json", "appsettings.json", "Code Default" }, resolutionOrder = new[] { "Environment Variable", $"appsettings.{environment.EnvironmentName}.json", "appsettings.json", "Code Default" }, effectiveSource = HasValue(env) ? $"Environment Variable ({envKey})" : HasValue(configured) ? "Configuration Provider" : defaultValue is not null ? "Code Default" : "Unresolved", hasDefault = defaultValue is not null, defaultValue, redacted = false };
    }
    private static object Secret(string id, string key, string name, bool required, string purpose, bool configured, string path, string? condition = null) => new { id, displayName = name, kind = "Secret", required, purpose, configurationKey = key, isConfigured = configured, safeDisplayValue = (string?)null, sensitive = true, conditionalOn = condition, allowedSources = new[] { "OpenBao", "approved Environment Variable override", "Secret0 bootstrap" }, resolutionOrder = new[] { "OpenBao", "approved Environment Variable override", "no plaintext fallback" }, effectiveSource = configured ? "Secure runtime source" : "Unresolved", hasDefault = false, defaultValue = (string?)null, secretPath = path, redacted = true };
    private static object Dependency(string id, string name, bool required, string criticality, string purpose, string degraded, string @interface, string? engine, string? endpoint, bool? reachable) => new { id, displayName = name, required, criticality, purpose, degradedBehavior = degraded, @interface, engine, endpoint, reachable, evidence = "Explicitly declared from RequestPortal code/configuration; no connection-string inference." };

    private async Task<object> DetectEnvironmentAsync(CancellationToken ct)
    {
        Process process = Process.GetCurrentProcess(); GCMemoryInfo memory = GC.GetGCMemoryInfo();
        string[] pathsToCheck = new[] { environment.ContentRootPath, configuration["Storage:Local:RootPath"], configuration["Security:DataProtectionKeysPath"], configuration["Messaging:DurableQueuePath"] }.Where(HasValue).Select(x => Path.GetFullPath(x!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        List<object> paths = []; List<object> disks = []; HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in pathsToCheck) { bool exists = Directory.Exists(path) || File.Exists(path); string target = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path; bool? writable = Directory.Exists(target) ? await IsWritableAsync(target, ct) : null; paths.Add(new { path, exists, writable }); try { string root = Path.GetPathRoot(path) ?? ""; if (root.Length > 0 && roots.Add(root)) { DriveInfo d = new(root); if (d.IsReady) disks.Add(new { path = root, freeBytes = d.AvailableFreeSpace, totalBytes = d.TotalSize }); } } catch { } }
        List<object> endpoints = []; string? cfg = First(configuration["Aegis:Configuration:Url"], configuration["AegisConfiguration:BaseUrl"], configuration["ConfigurationService:BaseUrl"]); if (HasValue(cfg)) endpoints.Add(new { name = "Aegis.Configuration", endpoint = cfg, reachable = await ReachableHttpAsync(cfg, ct) });
        if (configuration.GetValue("FfpManagerIntegration:Enabled", false) && configuration["FfpManagerIntegration:BaseUrl"] is { Length: > 0 } bridge) endpoints.Add(new { name = "FFP Manager Bridge", endpoint = bridge, reachable = await ReachableHttpAsync(bridge, ct) });
        if (configuration.GetValue("Authentication:StaffOidc:Enabled", false) && configuration["Authentication:StaffOidc:Authority"] is { Length: > 0 } authority) endpoints.Add(new { name = "Staff OIDC", endpoint = authority, reachable = await ReachableHttpAsync(authority, ct) });
        return new { operatingSystem = RuntimeInformation.OSDescription, operatingSystemVersion = Environment.OSVersion.VersionString, cpuArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), processorCount = Environment.ProcessorCount, availableMemoryBytes = memory.TotalAvailableMemoryBytes > 0 ? memory.TotalAvailableMemoryBytes : null, processWorkingSetBytes = process.WorkingSet64, runtimeVersion = RuntimeInformation.FrameworkDescription, networkAvailable = NetworkInterface.GetIsNetworkAvailable(), disks, paths, endpoints, hardware = Array.Empty<object>(), detectedAtUtc = DateTimeOffset.UtcNow };
    }

    private async Task<bool> SecretExistsAsync(string name, CancellationToken ct) { try { return HasValue(await secrets.GetAsync(name, ct)); } catch { return false; } }
    private string? OpenBaoAddress() => First(configuration["CommonSecrets:OpenBao:Address"], configuration["OpenBao:Address"], Environment.GetEnvironmentVariable("OPENBAO_ADDR"), Environment.GetEnvironmentVariable("VAULT_ADDR"));
    private bool SecretZeroDetected() { string? named = configuration["CommonSecrets:OpenBao:SecretIdEnvironmentVariable"]; string? file = configuration["CommonSecrets:OpenBao:SecretIdFile"]; return (!string.IsNullOrWhiteSpace(named) && HasValue(Environment.GetEnvironmentVariable(named))) || (!string.IsNullOrWhiteSpace(file) && File.Exists(file)) || HasValue(Environment.GetEnvironmentVariable("OPENBAO_SECRET_ID")); }
    private string? HostPort(string hostKey, string portKey, int defaultPort) { string? host = configuration[hostKey]; return HasValue(host) ? $"{host}:{configuration.GetValue(portKey, defaultPort)}" : null; }
    private static async Task<bool?> ReachableHttpAsync(string? endpoint, CancellationToken ct) { if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return null; try { using HttpClient c = new() { Timeout = TimeSpan.FromSeconds(3) }; using HttpResponseMessage _ = await c.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri), ct); return true; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { return false; } }
    private static async Task<bool?> ReachableTcpAsync(string? host, int port, CancellationToken ct) { if (string.IsNullOrWhiteSpace(host)) return null; try { using System.Net.Sockets.TcpClient client = new(); await client.ConnectAsync(host, port, ct); return true; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { return false; } }
    private static async Task<bool> IsWritableAsync(string path, CancellationToken ct) { string probe = Path.Combine(path, $".aegis-write-{Guid.NewGuid():N}"); try { await File.WriteAllTextAsync(probe, "", ct); File.Delete(probe); return true; } catch { try { if (File.Exists(probe)) File.Delete(probe); } catch { } return false; } }
    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool HasNonPlaceholderValue(string? value) => HasValue(value) && !value!.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);
    private static string? First(params string?[] values) => values.FirstOrDefault(HasValue);
}
