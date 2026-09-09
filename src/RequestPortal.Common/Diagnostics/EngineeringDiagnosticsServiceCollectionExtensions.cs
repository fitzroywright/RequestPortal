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

    // Temporary source-compatibility shim for the existing Program.cs call.
    public static IServiceCollection AddStarfleetDiagnostics(this IServiceCollection services)
        => services.AddEngineeringDiagnostics();
}
