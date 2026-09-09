using Common.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RequestPortal.Common.Time;

namespace RequestPortal.Common.Diagnostics;

public static class EngineeringDiagnosticsServiceCollectionExtensions
{
    public static IServiceCollection AddEngineeringDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddCommonDiagnostics();
        services.AddSingleton<RequestPortalEngineeringDiagnosticCatalog>();
        services.AddSingleton<IStartupFilter, EngineeringDiagnosticsStartupFilter>();
        return services;
    }

    [Obsolete("Use AddEngineeringDiagnostics. This compatibility shim will be removed after Program.cs migration.")]
    public static IServiceCollection AddStarfleetDiagnostics(this IServiceCollection services)
        => services.AddEngineeringDiagnostics();
}
