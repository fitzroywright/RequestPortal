using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Common.Diagnostics;

namespace RequestPortal.Infrastructure.Integration;

public static class FfpManagerIntegrationServiceCollectionExtensions
{
    public static IServiceCollection AddFfpManagerIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<FfpManagerBridgeOptions>()
            .Bind(configuration.GetSection(FfpManagerBridgeOptions.SectionName));

        services.AddHttpClient<IFfpManagerBridgeClient, FfpManagerBridgeClient>((serviceProvider, client) =>
        {
            FfpManagerBridgeOptions options = serviceProvider
                .GetRequiredService<IOptions<FfpManagerBridgeOptions>>()
                .Value;
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            }
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 300));
        });

        services.AddHttpClient("FfpManagerBridgeHealth", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        services.AddScoped<IFfpManagerIntegrationService, FfpManagerIntegrationService>();
        services.AddScoped<IStartupDiagnostic, FfpManagerBridgeStartupDiagnostic>();
        services.AddHostedService<FfpManagerStatusPollingWorker>();
        return services;
    }
}
