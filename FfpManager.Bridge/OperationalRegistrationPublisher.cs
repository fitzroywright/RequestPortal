using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Common.Secrets;
using Microsoft.AspNetCore.Hosting;

[assembly: HostingStartup(typeof(FfpManager.Bridge.BridgeOperationalRegistrationHostingStartup))]

namespace FfpManager.Bridge;

public sealed class BridgeOperationalRegistrationHostingStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder) => builder.ConfigureServices(services => services.AddHostedService<BridgeOperationalRegistrationPublisher>());
}

internal sealed class BridgeOperationalRegistrationPublisher(
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<BridgeOperationalRegistrationPublisher> logger) : BackgroundService
{
    private const string ApplicationId = "FfpManager.Bridge";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string baseUrl = First(configuration["Aegis:Configuration:Url"], configuration["AegisConfiguration:BaseUrl"], "http://localhost:5101")!;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (await TryPublishAsync(baseUrl, stoppingToken)) return;
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    private async Task<bool> TryPublishAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            ISecretProvider secrets = CommonSecretProviderFactory.Create(configuration);
            string? registrationKey = await secrets.GetAsync($"configuration/registration/{ApplicationId}", ct);
            if (string.IsNullOrWhiteSpace(registrationKey)) return false;

            string version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";
            string gateway = (configuration["Bridge:Gateway"] ?? "Fake").Trim();
            bool sqlMode = gateway.Equals("Sql", StringComparison.OrdinalIgnoreCase);
            string? siteId = First(configuration["Site:Id"], configuration["Deployment:SiteId"]);
            string instanceId = Environment.MachineName;

            object[] requirements =
            [
                Config("configuration-url", "Aegis:Configuration:Url", "System Configuration URL", false, "Aegis.Configuration endpoint.", "http://localhost:5101"),
                Secret("shared-secret", "Bridge:SharedSecret", "RequestPortal Bridge Shared Secret", environment.IsProduction(), "Shared authentication secret accepted by the bridge. Value is never published.", HasValue(configuration["Bridge:SharedSecret"]), "Bridge:SharedSecret"),
                Config("portal-url", "Bridge:RequestPortalBaseUrl", "RequestPortal Base URL", true, "RequestPortal endpoint used for callbacks."),
                Config("callback-timeout", "Bridge:CallbackTimeoutSeconds", "Callback Timeout", false, "RequestPortal callback timeout in seconds.", "30"),
                Config("max-artifact", "Bridge:MaxArtifactBytes", "Maximum Artifact Bytes", false, "Maximum artifact size accepted by the bridge.", "26214400"),
                Config("gateway", "Bridge:Gateway", "Gateway Mode", true, "Selects Fake or SQL FFP Manager gateway.", "Fake"),
                Secret("sql-connection", "Bridge:SqlConnectionString", "FFP Manager SQL Connection", sqlMode, "SQL Server connection string used by the SQL gateway. Value is never published.", HasValue(configuration["Bridge:SqlConnectionString"]), "Bridge:SqlConnectionString", "Bridge:Gateway=Sql"),
                Config("document-root", "Bridge:SupportingDocumentRoot", "Supporting Document Root", sqlMode, "FFP Manager supporting-document share path.", null, "Bridge:Gateway=Sql"),
                Config("username", "Bridge:UserName", "FFP Manager User Name", false, "Application user name written into FFP Manager audit fields.", "RequestPortal"),
                Config("created-by", "Bridge:CreatedBy", "FFP Manager Created By", false, "CreatedBy value written by the bridge.", "RequestPortal"),
                Config("initial-status", "Bridge:InitialApplicationStatus", "Initial Application Status", sqlMode, "FFP Manager status assigned to new applications.", "AWAITING APPROVAL", "Bridge:Gateway=Sql"),
                Config("default-document-type", "Bridge:DefaultDocumentType", "Default Document Type", false, "Default FFP Manager document type.", "Application Letter"),
                Config("information-note-type", "Bridge:InformationResponseNoteType", "Information Response Note Type", false, "FFP Manager note type used for information responses.", "RequestPortal.InformationResponse"),
                Config("reference-refresh", "Bridge:ReferenceDataRefreshSeconds", "Reference Data Refresh", false, "FFP Manager lookup refresh interval.", "300"),
                Config("allowed-hosts", "AllowedHosts", "Allowed Hosts", environment.IsProduction(), "ASP.NET Core host filtering."),
                Secret("secret-zero", "Secret0", "OpenBao Secret Zero", false, "Bootstrap material used by Common.Secrets when OpenBao is selected. Value is never published.", SecretZeroDetected(), "OpenBao bootstrap")
            ];

            List<object> dependencies =
            [
                Dependency("configuration", "Aegis.Configuration", false, "Feature", "Receives bridge operational contracts.", "Bridge remains operational without centralized visibility.", "HTTP/HTTPS JSON", "Aegis.Configuration", baseUrl, await ReachableHttpAsync(baseUrl, ct)),
                Dependency("requestportal", "RequestPortal", true, "Feature", "Receives status/information callbacks from the bridge.", "FFP Manager writes can proceed, but portal callbacks cannot be delivered.", "HTTPS/JSON", "RequestPortal", configuration["Bridge:RequestPortalBaseUrl"], await ReachableHttpAsync(configuration["Bridge:RequestPortalBaseUrl"], ct))
            ];
            if (sqlMode)
            {
                dependencies.Add(Dependency("ffp-sql", "FFP Manager Database", true, "Startup", "Reads/writes FFP Manager application and reference data.", "SQL gateway cannot initialize or process FFP Manager operations.", "Microsoft.Data.SqlClient", "Microsoft SQL Server", null, null));
                dependencies.Add(Dependency("supporting-documents", "FFP Manager Supporting Documents", true, "Feature", "Stores request artifacts for FFP Manager.", "Application records may be created but artifact storage fails.", "Filesystem/SMB", "Filesystem", configuration["Bridge:SupportingDocumentRoot"], null));
            }

            object registration = new
            {
                configuration = new { applicationId = ApplicationId, displayName = "FFP Manager Bridge", version, siteId, instanceId, description = "RequestPortal integration bridge for FFP Manager SQL data, artifacts and callbacks.", requirements },
                dependencies = new { applicationId = ApplicationId, version, siteId, instanceId, dependencies },
                environment = new { applicationId = ApplicationId, version, siteId, instanceId, environment = await DetectEnvironmentAsync(ct) }
            };

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            using HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/registrations/register");
            request.Headers.TryAddWithoutValidation("X-Aegis-Registration-Key", registrationKey);
            request.Content = new StringContent(JsonSerializer.Serialize(registration), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;
            logger.LogInformation("FFP Manager Bridge published configuration, dependency and detected-environment contracts.");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Unable to publish FFP Manager Bridge operational contracts."); return false; }
    }

    private object Config(string id, string key, string name, bool required, string purpose, string? defaultValue = null, string? condition = null)
    {
        string envKey = key.Replace(":", "__", StringComparison.Ordinal); string? envValue = Environment.GetEnvironmentVariable(envKey); string? configured = configuration[key]; string? value = First(envValue, configured, defaultValue);
        return new { id, displayName = name, kind = "Configuration", required, purpose, configurationKey = key, isConfigured = HasValue(value), safeDisplayValue = value, sensitive = false, conditionalOn = condition, allowedSources = new[] { "Environment Variable", $"appsettings.{environment.EnvironmentName}.json", "appsettings.json", "Code Default" }, resolutionOrder = new[] { "Environment Variable", $"appsettings.{environment.EnvironmentName}.json", "appsettings.json", "Code Default" }, effectiveSource = HasValue(envValue) ? $"Environment Variable ({envKey})" : HasValue(configured) ? "Configuration Provider" : defaultValue is not null ? "Code Default" : "Unresolved", hasDefault = defaultValue is not null, defaultValue, redacted = false };
    }
    private static object Secret(string id, string key, string name, bool required, string purpose, bool configured, string path, string? condition = null) => new { id, displayName = name, kind = "Secret", required, purpose, configurationKey = key, isConfigured = configured, safeDisplayValue = (string?)null, sensitive = true, conditionalOn = condition, allowedSources = new[] { "OpenBao/Common.Secrets", "Environment Variable", "secure deployment configuration", "Secret0 bootstrap" }, resolutionOrder = new[] { "Common.Secrets/OpenBao when configured", "approved Environment Variable override", "no display/plaintext fallback" }, effectiveSource = configured ? "Secure runtime source" : "Unresolved", hasDefault = false, defaultValue = (string?)null, secretPath = path, redacted = true };
    private static object Dependency(string id, string name, bool required, string criticality, string purpose, string degraded, string @interface, string? engine, string? endpoint, bool? reachable) => new { id, displayName = name, required, criticality, purpose, degradedBehavior = degraded, @interface, engine, endpoint, reachable, evidence = "Explicitly declared from FfpManager.Bridge code; SQL engine is declared by Microsoft.Data.SqlClient usage." };

    private async Task<object> DetectEnvironmentAsync(CancellationToken ct)
    {
        Process p = Process.GetCurrentProcess(); GCMemoryInfo memory = GC.GetGCMemoryInfo();
        List<object> paths = []; List<object> disks = []; HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in new[] { environment.ContentRootPath, configuration["Bridge:SupportingDocumentRoot"] }.Where(HasValue).Select(x => Path.GetFullPath(x!)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool exists = Directory.Exists(path) || File.Exists(path); string target = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path; bool? writable = Directory.Exists(target) ? await IsWritableAsync(target, ct) : null; paths.Add(new { path, exists, writable });
            try { string root = Path.GetPathRoot(path) ?? ""; if (root.Length > 0 && roots.Add(root)) { DriveInfo d = new(root); if (d.IsReady) disks.Add(new { path = root, freeBytes = d.AvailableFreeSpace, totalBytes = d.TotalSize }); } } catch { }
        }
        List<object> endpoints = []; string? portal = configuration["Bridge:RequestPortalBaseUrl"]; if (HasValue(portal)) endpoints.Add(new { name = "RequestPortal", endpoint = portal, reachable = await ReachableHttpAsync(portal, ct) });
        return new { operatingSystem = RuntimeInformation.OSDescription, operatingSystemVersion = Environment.OSVersion.VersionString, cpuArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), processorCount = Environment.ProcessorCount, availableMemoryBytes = memory.TotalAvailableMemoryBytes > 0 ? memory.TotalAvailableMemoryBytes : null, processWorkingSetBytes = p.WorkingSet64, runtimeVersion = RuntimeInformation.FrameworkDescription, networkAvailable = NetworkInterface.GetIsNetworkAvailable(), disks, paths, endpoints, hardware = Array.Empty<object>(), detectedAtUtc = DateTimeOffset.UtcNow };
    }

    private static bool SecretZeroDetected() => new[] { "OPENBAO_SECRET_ID", "OPENBAO_TOKEN", "VAULT_TOKEN" }.Any(name => HasValue(Environment.GetEnvironmentVariable(name)));
    private static async Task<bool?> ReachableHttpAsync(string? endpoint, CancellationToken ct) { if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return null; try { using HttpClient c = new() { Timeout = TimeSpan.FromSeconds(3) }; using HttpResponseMessage _ = await c.SendAsync(new HttpRequestMessage(HttpMethod.Head, uri), ct); return true; } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch { return false; } }
    private static async Task<bool> IsWritableAsync(string path, CancellationToken ct) { string probe = Path.Combine(path, $".aegis-write-{Guid.NewGuid():N}"); try { await File.WriteAllTextAsync(probe, "", ct); File.Delete(probe); return true; } catch { try { if (File.Exists(probe)) File.Delete(probe); } catch { } return false; } }
    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);
    private static string? First(params string?[] values) => values.FirstOrDefault(HasValue);
}
