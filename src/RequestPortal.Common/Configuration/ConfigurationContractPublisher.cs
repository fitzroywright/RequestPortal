using System.Text;
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

        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Configuration contract was not found at {ContractPath}; RequestPortal will continue without publishing it.", path);
                return;
            }

            string? key = await secrets.GetAsync("configuration/registration/RequestPortal", stoppingToken);
            if (string.IsNullOrWhiteSpace(key))
            {
                logger.LogWarning("Configuration registration credential is unavailable; RequestPortal will continue without publishing its contract.");
                return;
            }

            string json = await File.ReadAllTextAsync(path, stoppingToken);
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
            using HttpRequestMessage request = new(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/contracts/register");
            request.Headers.TryAddWithoutValidation("X-Configuration-Registration-Key", key);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.SendAsync(request, stoppingToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Configuration service rejected RequestPortal contract publication with HTTP {StatusCode}; RequestPortal remains operational.", (int)response.StatusCode);
                return;
            }

            logger.LogInformation("Published RequestPortal configuration contract.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to publish RequestPortal configuration contract; RequestPortal remains operational.");
        }
    }
}
