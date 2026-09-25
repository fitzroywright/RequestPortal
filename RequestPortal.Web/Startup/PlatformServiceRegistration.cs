using System.Threading.RateLimiting;
using Common.Secrets;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Documents;
using RequestPortal.Common.Time;
using RequestPortal.Infrastructure.Documents;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Web.Startup;

internal static class PlatformServiceRegistration
{
    public static void AddPersistence(
        WebApplicationBuilder builder
        , string connectionString)
    {
        AddUploadLimits(builder);

        builder.Services.AddDbContext<RequestPortalDbContext>(
            options => options.UseNpgsql(connectionString));
    }

    public static void AddPlatform(
        WebApplicationBuilder builder
        , CommonSecretsBootstrapRuntime secretsRuntime)
    {
        AddSecurityServices(
            builder
            , secretsRuntime);
        AddDataProtection(builder);
        AddProxyHeaders(builder);
        AddRateLimiting(builder);
        AddStorage(builder);
    }

    private static void AddUploadLimits(
        WebApplicationBuilder builder)
    {
        long uploadMaxBytes =
            builder.Configuration.GetValue<long?>("Uploads:MaxBytes")
            ?? 25L * 1024L * 1024L;

        builder.WebHost.ConfigureKestrel(
            options => options.Limits.MaxRequestBodySize =
                uploadMaxBytes + (1024L * 1024L));

        builder.Services.Configure<FormOptions>(
            options =>
                options.MultipartBodyLengthLimit =
                    uploadMaxBytes);
    }

    private static void AddSecurityServices(
        WebApplicationBuilder builder
        , CommonSecretsBootstrapRuntime secretsRuntime)
    {
        Common.Security.Integration.SecurityServiceCollectionExtensions
            .AddCommonAuthorization<RequestPortalAuthorizationStore>(
                builder.Services);

        Common.Security.Integration.SecurityServiceCollectionExtensions
            .AddCommonSecurityDiagnostics(
                builder.Services);

        secretsRuntime.Register(builder.Services);

        Common.Secrets.ServiceCollectionExtensions
            .AddCommonSecretsDiagnostics(
                builder.Services);
    }

    private static void AddDataProtection(
        WebApplicationBuilder builder)
    {
        string dataProtectionPath =
            builder.Configuration[
                "Security:DataProtectionKeysPath"]
            ?? "App_Data/keys";

        builder.Services
            .AddDataProtection()
            .PersistKeysToFileSystem(
                new DirectoryInfo(
                    Path.GetFullPath(
                        dataProtectionPath)))
            .SetApplicationName(
                "RequestPortal");
    }

    private static void AddProxyHeaders(
        WebApplicationBuilder builder)
    {
        builder.Services.Configure<ForwardedHeadersOptions>(
            options =>
            {
                options.ForwardedHeaders =
                    ForwardedHeaders.XForwardedFor
                    | ForwardedHeaders.XForwardedProto;
            });
    }

    private static void AddRateLimiting(
        WebApplicationBuilder builder)
    {
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode =
                StatusCodes.Status429TooManyRequests;

            options.GlobalLimiter =
                PartitionedRateLimiter.Create<HttpContext, string>(
                    context =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            context.Connection.RemoteIpAddress?.ToString()
                                ?? "unknown"
                            , _ => new FixedWindowRateLimiterOptions
                            {
                                PermitLimit = 120
                                , Window =
                                    TimeSpan.FromMinutes(1)
                                , QueueLimit = 0
                                , AutoReplenishment = true
                            }));
        });
    }

    private static void AddStorage(
        WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<
            IClock
            , SystemClock>();

        string storageRoot =
            builder.Configuration[
                "Storage:Local:RootPath"]
            ?? "App_Data/storage";

        Common.Storage.StorageServiceCollectionExtensions
            .AddCommonStorage(
                builder.Services
                , storageRoot);

        builder.Services.AddSingleton<
            IDocumentUploadSecurity
            , DocumentUploadSecurity>();

        builder.Services.AddSingleton<
            IMalwareScanner
            , ClamAvMalwareScanner>();
    }
}
