using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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
        string? baseUrl = configuration["ConfigurationService:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogInformation("Configuration contract publication is disabled because ConfigurationService:BaseUrl is not configured.");
            return;
        }

        string path = configuration["ConfigurationService:ContractPath"]
            ?? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "configuration", "RequestPortal.configuration-contract.json"));

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await TryPublishAsync(baseUrl, path, stoppingToken))
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

    private async Task<bool> TryPublishAsync(string baseUrl, string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Configuration contract was not found at {ContractPath}; RequestPortal remains operational and will retry publication.", path);
                return false;
            }

            string? key = await secrets.GetAsync("configuration/registration/RequestPortal", cancellationToken);
            if (string.IsNullOrWhiteSpace(key))
            {
                logger.LogWarning("Configuration registration credential is unavailable; RequestPortal remains operational and will retry publication.");
                return false;
            }

            string json = await BuildRuntimeContractAsync(path, cancellationToken);
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            using HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/contracts/register");
            request.Headers.TryAddWithoutValidation("X-Configuration-Registration-Key", key);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Configuration service rejected RequestPortal contract publication with HTTP {StatusCode}; RequestPortal remains operational and will retry publication.", (int)response.StatusCode);
                return false;
            }

            logger.LogInformation("RequestPortal configuration requirements and configured state are registered with Aegis.Configuration.");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to publish RequestPortal configuration requirements; RequestPortal remains operational and will retry publication.");
            return false;
        }
    }

    private async Task<string> BuildRuntimeContractAsync(string path, CancellationToken cancellationToken)
    {
        JsonObject contract = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("The RequestPortal configuration contract is not a JSON object.");

        contract["version"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? contract["version"]?.GetValue<string>() ?? "unknown";

        string? siteId = configuration["Site:Id"];
        contract["siteId"] = NullIfWhiteSpace(siteId);
        contract["instanceId"] = NullIfWhiteSpace(configuration["Service:Identity"] ?? Environment.MachineName);

        if (contract["requirements"] is JsonArray requirements)
        {
            foreach (JsonNode? node in requirements)
            {
                if (node is not JsonObject requirement)
                {
                    continue;
                }

                string id = requirement["id"]?.GetValue<string>() ?? string.Empty;
                bool configured = id switch
                {
                    "database-connection" => await HasSecretAsync("ConnectionStrings:RequestPortal", cancellationToken),
                    "storage-root" => HasValue("Storage:Local:RootPath"),
                    "data-protection-keys" => HasValue("Security:DataProtectionKeysPath"),
                    "openbao-address" => HasAnyValue("CommonSecrets:OpenBao:Address", "OpenBao:Address", "OPENBAO_ADDR", "VAULT_ADDR"),
                    "openbao-role" => HasAnyValue("CommonSecrets:OpenBao:RoleId", "OpenBao:RoleId", "OPENBAO_ROLE_ID") || await HasSecretAsync("CommonSecrets:OpenBao:RoleId", cancellationToken),
                    "staff-oidc-authority" => HasValue("Authentication:StaffOidc:Authority"),
                    "staff-oidc-client-id" => HasValue("Authentication:StaffOidc:ClientId"),
                    "staff-oidc-client-secret" => await HasSecretAsync("Authentication:StaffOidc:ClientSecret", cancellationToken),
                    "staff-reviewer-groups" => HasValue("Authentication:ActiveDirectory:ReviewerGroups"),
                    "staff-administrator-groups" => HasValue("Authentication:ActiveDirectory:AdministratorGroups"),
                    "storage-malware-scanner" => HasValue("Uploads:MalwareScanning:Host"),
                    "smtp-host" => HasValue("Notifications:SmtpHost"),
                    "smtp-from" => HasValue("Notifications:FromAddress"),
                    "smtp-password" => await HasSecretAsync("Notifications:SmtpPassword", cancellationToken),
                    "messaging-queue" => HasValue("Messaging:DurableQueuePath"),
                    "site-id" => !string.IsNullOrWhiteSpace(siteId),
                    _ => false
                };

                requirement["isConfigured"] = configured;
                requirement.Remove("safeDisplayValue");

                if (!IsSensitive(requirement) && TryGetSafeDisplayValue(id, out string? safeValue))
                {
                    requirement["safeDisplayValue"] = safeValue;
                }
            }
        }

        return contract.ToJsonString();
    }

    private bool HasValue(string key) => !string.IsNullOrWhiteSpace(configuration[key]);

    private bool HasAnyValue(params string[] keys)
    {
        foreach (string key in keys)
        {
            if (HasValue(key) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> HasSecretAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(await secrets.GetAsync(name, cancellationToken));
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Unable to confirm whether configuration secret {SecretName} is available.", name);
            return false;
        }
    }

    private bool TryGetSafeDisplayValue(string requirementId, out string? value)
    {
        value = requirementId switch
        {
            "storage-root" => configuration["Storage:Local:RootPath"],
            "data-protection-keys" => configuration["Security:DataProtectionKeysPath"],
            "openbao-address" => configuration["CommonSecrets:OpenBao:Address"] ?? configuration["OpenBao:Address"] ?? Environment.GetEnvironmentVariable("OPENBAO_ADDR") ?? Environment.GetEnvironmentVariable("VAULT_ADDR"),
            "staff-oidc-authority" => configuration["Authentication:StaffOidc:Authority"],
            "staff-oidc-client-id" => configuration["Authentication:StaffOidc:ClientId"],
            "storage-malware-scanner" => configuration["Uploads:MalwareScanning:Host"],
            "smtp-host" => configuration["Notifications:SmtpHost"],
            "smtp-from" => configuration["Notifications:FromAddress"],
            "messaging-queue" => configuration["Messaging:DurableQueuePath"],
            "site-id" => configuration["Site:Id"],
            _ => null
        };

        value = NullIfWhiteSpace(value);
        return value is not null;
    }

    private static bool IsSensitive(JsonObject requirement) => requirement["sensitive"]?.GetValue<bool>() == true;

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
