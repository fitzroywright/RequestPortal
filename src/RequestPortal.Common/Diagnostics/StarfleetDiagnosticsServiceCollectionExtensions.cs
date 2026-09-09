using Common.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RequestPortal.Common.Time;

namespace RequestPortal.Common.Diagnostics;

public static class StarfleetDiagnosticsServiceCollectionExtensions
{
    public static IServiceCollection AddStarfleetDiagnostics(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IStarfleetDiagnosticSink, LoggingDiagnosticSink>();
        services.AddSingleton<IStarfleetDiagnostics, StarfleetDiagnostics>();
        services.AddCommonDiagnostics();
        services.AddSingleton<RequestPortalEngineeringDiagnosticCatalog>();
        return services;
    }
}
