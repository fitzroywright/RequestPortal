using System.Threading.RateLimiting;
using Common.Secrets;
using Common.Storage;
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
using RequestPortal.Web.Configuration;

namespace RequestPortal.Web.Startup;

internal static class PlatformServiceRegistration
{
    public static void AddPersistence(
        WebApplicationBuilder builder
        , string connectionString)
    {
        AddUploadLimits(builder);
        builder.Services.AddScoped<RequestPortal.Web.Security.StaffAdministrationService>();
        builder.Services.AddScoped<RequestPortal.Web.Security.ApplicantIdentityAdministration>();
        builder.Services.AddScoped<RequestPortal.Web.Security.StaffPreProvisioningService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<RequestPortal.Web.Security.StaffDirectoryOptions>(
            builder.Configuration.GetSection(RequestPortal.Web.Security.StaffDirectoryOptions.SectionName));
        builder.Services.AddHttpClient<RequestPortal.Web.Security.IStaffDirectoryLookup, RequestPortal.Web.Security.HttpStaffDirectoryLookup>(client => {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.MaxResponseContentBufferSize = 1024 * 1024;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton(new RequestPortal.Web.Security.RequestPortalAccounts(connectionString));
        builder.Services.AddSingleton<global::Common.Security.Abstractions.ILocalCredentialStore>(sp => sp.GetRequiredService<RequestPortal.Web.Security.RequestPortalAccounts>().Staff);
        builder.Services.AddSingleton(sp=>new global::Common.Security.Services.AccountProvisioningService(sp.GetRequiredService<RequestPortal.Web.Security.RequestPortalAccounts>().Staff));
        global::Common.Security.Hosting.AccountSecurityHosting.AddCommonAuthenticationRateLimiting(builder.Services);
        builder.Services.AddSingleton<global::Common.Security.Abstractions.IAccountStore>(sp => sp.GetRequiredService<RequestPortal.Web.Security.RequestPortalAccounts>().Staff);

        builder.Services.AddDbContext<RequestPortalDbContext>(
            options => options.UseNpgsql(connectionString));
    }

    public static void AddPlatform(
        WebApplicationBuilder builder
        , CommonSecretsBootstrapRuntime secretsRuntime
        , RuntimeSecrets runtimeSecrets)
    {
        AddSecurityServices(
            builder
            , secretsRuntime);
        AddDataProtection(builder);
        AddProxyHeaders(builder);
        AddRateLimiting(builder);
        AddStorage(builder, runtimeSecrets);
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
        global::Common.Security.Integration.SecurityServiceCollectionExtensions
            .AddCommonAuthorization<RequestPortalAuthorizationStore>(
                builder.Services);

        global::Common.Security.Integration.SecurityServiceCollectionExtensions
            .AddCommonSecurityDiagnostics(
                builder.Services);

        secretsRuntime.Register(builder.Services);

        global::Common.Secrets.ServiceCollectionExtensions
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
            options.OnRejected = async (rejected, cancellationToken) =>
            {
                var context = rejected.HttpContext;
                if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers.CacheControl = "no-store";
                // An empty, untyped rejection can be treated as a download for /Staff.
                if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path.StartsWithSegments("/Staff"))
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    string retryUrl = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(
                        context.Request.PathBase + context.Request.Path + context.Request.QueryString);
                    await context.Response.WriteAsync($"<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Please wait - Request Portal</title><h1>Please wait</h1><p>Too many requests. Please wait a minute, then <a href=\"{retryUrl}\">retry this page</a>.</p></html>", cancellationToken);
                }
                else
                {
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    await context.Response.WriteAsync("Too many requests. Please wait a minute and try again.", cancellationToken);
                }
            };

            options.GlobalLimiter =
                PartitionedRateLimiter.Create<HttpContext, string>(
                    context =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            // Staff sharing an office/NAT address must not consume
                            // one another's navigation quota. Authentication runs first.
                            context.User.Identity?.IsAuthenticated == true &&
                            context.User.HasClaim(RequestPortal.Web.Security.StaffOidcOptions.StaffSurfaceClaim, RequestPortal.Web.Security.StaffOidcOptions.StaffSurfaceValue) &&
                            context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value is { } staffId
                                ? "staff:" + staffId
                                : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown")
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
        WebApplicationBuilder builder
        , RuntimeSecrets runtimeSecrets)
    {
        builder.Services.AddSingleton<
            IClock
            , SystemClock>();

        StorageTargetOptions target = StorageTargetOptions.FromConfiguration(
            builder.Configuration
            , runtimeSecrets.StorageSharePointClientSecret);

        if (target.Kind is StorageTargetKind.LocalFolder or StorageTargetKind.NetworkFolder &&
            !Path.IsPathRooted(target.RootPath))
        {
            target = target with
            {
                RootPath = Path.GetFullPath(
                    Path.Combine(
                        builder.Environment.ContentRootPath
                        , target.RootPath!))
            };
        }

        global::Common.Storage.StorageServiceCollectionExtensions
            .AddCommonStorage(
                builder.Services
                , target);

        builder.Services.AddSingleton<
            IDocumentUploadSecurity
            , DocumentUploadSecurity>();

        builder.Services.AddSingleton<
            IMalwareScanner
            , ClamAvMalwareScanner>();
    }
}
