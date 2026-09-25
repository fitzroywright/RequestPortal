using Common.Registration;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Portal;

namespace RequestPortal.Web.Startup;

internal static class OperationsServiceRegistration
{
    public static string Add(WebApplicationBuilder builder)
    {
        string instanceId =
            builder.Configuration["Service:Identity"]?.Trim()
            ?? Environment.MachineName;

        string identityFile =
            builder.Configuration["Aegis:Registration:IdentityFile"]
            ?? (OperatingSystem.IsWindows()
                ? Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData)
                    , "Aegis"
                    , "RequestPortal"
                    , "registration-identity.json")
                : "/var/lib/aegis/requestportal/registration-identity.json");

        builder.Services.AddAegisOperationsLogging(
            builder.Configuration
            , "RequestPortal"
            , instanceId
            , identityFile
            , builder.Environment.EnvironmentName);

        builder.Services.AddHostedService<OperationsTelemetryPublisher>();
        builder.Services.AddSingleton<RequestPortalOperationsFlowPublisher>();
        builder.Services.AddScoped<IPortalWorkflowService, AuditedPortalWorkflowService>();
        builder.Services.AddSingleton<IRequestPortalOperationsFlowPublisher>(
            services => services.GetRequiredService<RequestPortalOperationsFlowPublisher>());
        builder.Services.AddHostedService(
            services => services.GetRequiredService<RequestPortalOperationsFlowPublisher>());

        return instanceId;
    }
}
