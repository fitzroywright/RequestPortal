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
        string? baseUrl = configuration["AegisConfiguration:BaseUrl"] ?? configuration["ConfigurationService:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            logger.LogInformation("Aegis.Configuration publication is disabled because no Configuration base URL is configured.");
            return;
        }

        string path = configuration["AegisConfiguration:ContractPath"]
            ?? configuration["ConfigurationService:ContractPath"]
            ?? Path.Combine(environment.ContentRootPath, "configuration", "RequestPortal.configuration-contract.json");

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

            JsonObject contract = await BuildRuntimeContractAsync(path, cancellationToken);
            using HttpClient client = new();
            var registrar = new ConfigurationRegistrar(
                client,
                new RegistrationOptions(
                    new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute),
                    key,
                    TimeSpan.FromSeconds(10)));

            RegistrationResult result = await registrar.RegisterContractAsync(contract, cancellationToken);
            if (!result.Succeeded)
            {
                logger.LogWarning("Aegis.Configuration registration failed for RequestPortal: {RegistrationError}; RequestPortal remains operational and will retry publication.", result.Error ?? "Unknown registration failure.");
                return false;
            }

            logger.LogInformation("RequestPortal configuration requirements and configured state registered with Aegis.Configuration with HTTP {StatusCode}.", (int?)result.StatusCode);
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

    private async Task<JsonObject> BuildRuntimeContractAsync(string path, CancellationToken cancellationToken)
    {
        JsonObject contract = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))?.AsObject()
            ?? throw new InvalidDataException("The RequestPortal configuration contract is not a JSON object.");

        contract["version"] = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? contract["version"]?.GetValue<string>()
            ?? "unknown";
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
                    "database-connection" => await HasSecretAsync("ConnectionStrings:RequestPortal", cancellationToken) || HasNonPlaceholderValue(configuration.GetConnectionString("RequestPortal")),
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Unable to confirm whether configuration secret {SecretName} is available.", name);
            return false;
        }
    }

    private static bool HasNonPlaceholderValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
