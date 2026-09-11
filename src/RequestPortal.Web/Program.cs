using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Beneficiaries;
using RequestPortal.Application.Countries;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Fulfilment;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Notifications;
using RequestPortal.Application.Organizations;
using RequestPortal.Application.Portal;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Reporting;
using RequestPortal.Application.Requests;
using RequestPortal.Application.Security;
using RequestPortal.Common.Configuration;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Time;
using RequestPortal.Infrastructure.Administration;
using RequestPortal.Infrastructure.Auditing;
using RequestPortal.Infrastructure.Beneficiaries;
using RequestPortal.Infrastructure.Countries;
using RequestPortal.Infrastructure.Diagnostics;
using RequestPortal.Infrastructure.Documents;
using RequestPortal.Infrastructure.Fulfilment;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Notifications;
using RequestPortal.Infrastructure.Organizations;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Portal;
using RequestPortal.Infrastructure.ReferenceData;
using RequestPortal.Infrastructure.Reporting;
using RequestPortal.Infrastructure.Requests;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Demo;
using RequestPortal.Web.Security;
using RequestPortal.Web.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
Common.Secrets.CommonSecretsBootstrapRuntime? secretsRuntime = null;

if (builder.Environment.IsProduction())
{
    secretsRuntime = Common.Secrets.CommonSecretsBootstrapRuntime.Create(builder.Configuration);
    await HydrateProductionSecretsAsync(builder.Configuration, secretsRuntime.Provider);
}

string connectionString = builder.Configuration.GetConnectionString("RequestPortal")
    ?? throw new InvalidOperationException("SF-CONFIG-DB-001: PostgreSQL connection string 'RequestPortal' is not configured.");

long uploadMaxBytes = builder.Configuration.GetValue<long?>("Uploads:MaxBytes") ?? 25L * 1024L * 1024L;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = uploadMaxBytes + (1024L * 1024L));
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = uploadMaxBytes);

builder.Services.AddDbContext<RequestPortalDbContext>(options => options.UseNpgsql(connectionString));

builder.Services
    .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<RequestPortalDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.Name = "RequestPortal.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});

AuthenticationBuilder authentication = builder.Services.AddAuthentication();

string? googleClientId = builder.Configuration["Authentication:Google:ClientId"];
string? googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    authentication.AddGoogle(options => { options.ClientId = googleClientId; options.ClientSecret = googleClientSecret; });
}

string? microsoftClientId = builder.Configuration["Authentication:Microsoft:ClientId"];
string? microsoftClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"];
if (!string.IsNullOrWhiteSpace(microsoftClientId) && !string.IsNullOrWhiteSpace(microsoftClientSecret))
{
    authentication.AddMicrosoftAccount(options => { options.ClientId = microsoftClientId; options.ClientSecret = microsoftClientSecret; });
}

string? facebookAppId = builder.Configuration["Authentication:Facebook:AppId"];
string? facebookAppSecret = builder.Configuration["Authentication:Facebook:AppSecret"];
if (!string.IsNullOrWhiteSpace(facebookAppId) && !string.IsNullOrWhiteSpace(facebookAppSecret))
{
    authentication.AddFacebook(options => { options.AppId = facebookAppId; options.AppSecret = facebookAppSecret; });
}

builder.Services.AddOptions<ActiveDirectoryOptions>().Bind(builder.Configuration.GetSection(ActiveDirectoryOptions.SectionName));
builder.Services.AddOptions<NotificationOptions>().Bind(builder.Configuration.GetSection(NotificationOptions.SectionName));

ActiveDirectoryOptions requestPortalAd = builder.Configuration
    .GetSection(ActiveDirectoryOptions.SectionName)
    .Get<ActiveDirectoryOptions>() ?? new ActiveDirectoryOptions();
string[] authorizationGroups = requestPortalAd.AdministratorGroups
    .Concat(requestPortalAd.ReviewerGroups)
    .Where(group => !string.IsNullOrWhiteSpace(group))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
Common.Security.Options.ActiveDirectoryAuthenticationOptions commonAd = new()
{
    Domain = requestPortalAd.Domain ?? string.Empty,
    SearchBase = requestPortalAd.SearchBase ?? string.Empty,
    Port = requestPortalAd.Port,
    TimeoutSeconds = requestPortalAd.TimeoutSeconds,
    Servers = string.IsNullOrWhiteSpace(requestPortalAd.Server) ? [] : [requestPortalAd.Server],
    AdministrativeGroups = authorizationGroups
};
Common.Security.Integration.SecurityServiceCollectionExtensions.AddCommonSecurity(builder.Services, commonAd);
Common.Security.Integration.SecurityServiceCollectionExtensions.AddCommonSecurityDiagnostics(builder.Services);

if (secretsRuntime is not null)
{
    secretsRuntime.Register(builder.Services);
}
else
{
    Common.Secrets.ServiceCollectionExtensions.AddCommonSecrets(builder.Services, builder.Configuration);
}
Common.Secrets.ServiceCollectionExtensions.AddCommonSecretsDiagnostics(builder.Services);

string dataProtectionPath = builder.Configuration["Security:DataProtectionKeysPath"] ?? "App_Data/keys";
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(dataProtectionPath)))
    .SetApplicationName("RequestPortal");

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddSingleton<IClock, RequestPortal.Common.Time.SystemClock>();
string storageRoot = builder.Configuration["Storage:Local:RootPath"] ?? "App_Data/storage";
Common.Storage.StorageServiceCollectionExtensions.AddCommonStorage(builder.Services, storageRoot);
builder.Services.AddSingleton<IDocumentUploadSecurity, DocumentUploadSecurity>();

NotificationOptions notificationOptions = builder.Configuration
    .GetSection(NotificationOptions.SectionName)
    .Get<NotificationOptions>() ?? new NotificationOptions();
string messagingQueuePath = builder.Configuration["Messaging:DurableQueuePath"]
    ?? Path.Combine("App_Data", "common-messaging");
Common.Messaging.Hosting.ServiceCollectionExtensions.AddCommonMessagingSecrets(builder.Services);
Common.Messaging.Hosting.ChannelServiceCollectionExtensions.AddSmtpMessagingChannel(
    builder.Services,
    new Common.Messaging.Channels.Smtp.SmtpMessageOptions
    {
        Host = notificationOptions.SmtpHost ?? string.Empty,
        Port = notificationOptions.SmtpPort,
        EnableSsl = notificationOptions.EnableSsl,
        FromAddress = notificationOptions.FromAddress ?? string.Empty,
        UserName = notificationOptions.UserName ?? string.Empty,
        PasswordSecretName = notificationOptions.PasswordSecretName
    });
Common.Messaging.Hosting.ServiceCollectionExtensions.AddCommonMessagingQueuedDelivery(
    builder.Services,
    new Common.Messaging.ExternalDeliveryOptions
    {
        DeliveryMode = Common.Messaging.ExternalDeliveryMode.ParallelByRecipient,
        MaxConcurrency = 4,
        MaxAttempts = 5,
        InitialRetryDelay = TimeSpan.FromSeconds(5),
        MaxRetryDelay = TimeSpan.FromMinutes(5),
        DurableQueuePath = messagingQueuePath
    },
    durable: true);
Common.Messaging.Hosting.ServiceCollectionExtensions.AddCommonMessagingDiagnostics(builder.Services);
builder.Services.AddScoped<INotificationSender, CommonQueuedNotificationSender>();

builder.Services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();
builder.Services.AddScoped<ICountryPolicyService, CountryPolicyService>();
builder.Services.AddScoped<IRequestReferenceDataService, RequestReferenceDataService>();
builder.Services.AddScoped<IOrganizationMembershipGovernanceService, OrganizationMembershipGovernanceService>();
builder.Services.AddScoped<IOrganizationAccessService, OrganizationAccessService>();
builder.Services.AddScoped<IOrganizationProfileService, OrganizationProfileService>();
builder.Services.AddScoped<PortalWorkflowService>();
builder.Services.AddScoped<IPortalWorkflowService, AuditedPortalWorkflowService>();
builder.Services.AddScoped<IStaffOrganizationDirectoryService, StaffOrganizationDirectoryService>();
builder.Services.AddScoped<IStaffOrganizationService, StaffOrganizationService>();
builder.Services.AddScoped<IStaffOperationsService, StaffOperationsService>();
builder.Services.AddScoped<IStaffReportingService, StaffReportingService>();
builder.Services.AddScoped<IBusinessAuditWriter, BusinessAuditWriter>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IBeneficiaryService, BeneficiaryService>();
builder.Services.AddScoped<IRequestDraftService, RequestDraftService>();
builder.Services.AddScoped<IProjectProposalService, ProjectProposalService>();
builder.Services.AddScoped<IProjectPlanningService, ProjectPlanningService>();
builder.Services.AddScoped<IRequestLifecycleService, RequestLifecycleService>();
builder.Services.AddScoped<IFulfilmentQueueService, FulfilmentQueueService>();
builder.Services.AddScoped<IIntegrationStatusService, IntegrationStatusService>();
builder.Services.AddScoped<StaffSignInService>();
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxHostedService>();
builder.Services.AddHostedService<ConfigurationContractPublisher>();
builder.Services.AddScoped<DemoDatabaseBootstrapper>();
builder.Services.AddScoped<DemoDataSeeder>();
builder.Services.AddScoped<DemoReadinessValidator>();
builder.Services.AddEngineeringDiagnostics();
builder.Services.AddScoped<IStartupDiagnostic, PostgreSqlStartupDiagnostic>();
builder.Services.AddScoped<IStartupDiagnostic, ActiveDirectoryStartupDiagnostic>();
builder.Services.AddScoped<StartupDiagnosticRunner>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(PortalPolicies.FfpStaff, policy => policy.RequireRole(PortalRoles.FfpAdministrator, PortalRoles.FfpReviewer));
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Dashboard");
    options.Conventions.AuthorizeFolder("/Staff", PortalPolicies.FfpStaff);
    options.Conventions.AuthorizeFolder("/Organizations");
    options.Conventions.AuthorizeFolder("/Requests");
});

WebApplication app = builder.Build();

if (app.Environment.IsProduction())
{
    ValidateProductionConfiguration(app.Configuration);
    await ApplyProductionMigrationsAsync(app);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

await PrepareDemoAsync(app);
await ReportStartupDiagnosticsAsync(app);
try
{
    await app.RunAsync();
}
finally
{
    if (secretsRuntime is not null)
    {
        await secretsRuntime.DisposeAsync();
    }
}

static async Task HydrateProductionSecretsAsync(
    IConfiguration configuration,
    Common.Secrets.ISecretProvider provider)
{
    string[] keys =
    [
        "ConnectionStrings:RequestPortal",
        "Authentication:Google:ClientId",
        "Authentication:Google:ClientSecret",
        "Authentication:Microsoft:ClientId",
        "Authentication:Microsoft:ClientSecret",
        "Authentication:Facebook:AppId",
        "Authentication:Facebook:AppSecret"
    ];

    foreach (string key in keys)
    {
        string? value = await provider.GetAsync(key);
        if (!string.IsNullOrWhiteSpace(value))
        {
            configuration[key] = value;
        }
    }
}

static void ValidateProductionConfiguration(IConfiguration configuration)
{
    string storageRoot = configuration["Storage:Local:RootPath"] ?? string.Empty;
    string keyPath = configuration["Security:DataProtectionKeysPath"] ?? string.Empty;
    string allowedHosts = configuration["AllowedHosts"] ?? string.Empty;
    bool malwareScanningEnabled = configuration.GetValue("Uploads:MalwareScanning:Enabled", false);
    bool notificationsEnabled = configuration.GetValue("Notifications:Enabled", false);
    string[] reviewerGroups = configuration.GetSection("Authentication:ActiveDirectory:ReviewerGroups").Get<string[]>() ?? Array.Empty<string>();
    string[] administratorGroups = configuration.GetSection("Authentication:ActiveDirectory:AdministratorGroups").Get<string[]>() ?? Array.Empty<string>();
    bool hasExternalProvider =
        (!string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientId"]) && !string.IsNullOrWhiteSpace(configuration["Authentication:Google:ClientSecret"])) ||
        (!string.IsNullOrWhiteSpace(configuration["Authentication:Microsoft:ClientId"]) && !string.IsNullOrWhiteSpace(configuration["Authentication:Microsoft:ClientSecret"])) ||
        (!string.IsNullOrWhiteSpace(configuration["Authentication:Facebook:AppId"]) && !string.IsNullOrWhiteSpace(configuration["Authentication:Facebook:AppSecret"]));

    if (string.IsNullOrWhiteSpace(storageRoot) || storageRoot.Contains("App_Data", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("SF-PROD-STORAGE-001: Production document storage must be a durable external filesystem path or network share.");
    if (string.IsNullOrWhiteSpace(keyPath) || keyPath.Contains("App_Data", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("SF-PROD-DP-001: Production Data Protection keys must be persisted outside the application deployment directory.");
    if (string.IsNullOrWhiteSpace(configuration["CommonSecrets:OpenBao:Address"]))
        throw new InvalidOperationException("SF-PROD-SECRETS-001: Common.Secrets OpenBao is not configured.");
    if (string.IsNullOrWhiteSpace(configuration["CommonSecrets:OpenBao:RoleId"]))
        throw new InvalidOperationException("SF-PROD-SECRETS-002: Common.Secrets OpenBao AppRole RoleId is not configured.");
    if (allowedHosts == "*" || string.IsNullOrWhiteSpace(allowedHosts))
        throw new InvalidOperationException("SF-PROD-HOSTS-001: AllowedHosts must be restricted in production.");
    if (!malwareScanningEnabled || string.IsNullOrWhiteSpace(configuration["Uploads:MalwareScanning:Host"]))
        throw new InvalidOperationException("SF-PROD-UPLOAD-001: Malware scanning must be configured and enabled in production.");
    if (string.IsNullOrWhiteSpace(configuration["Authentication:ActiveDirectory:Server"]) ||
        string.IsNullOrWhiteSpace(configuration["Authentication:ActiveDirectory:SearchBase"]) ||
        reviewerGroups.Length == 0 || administratorGroups.Length == 0)
        throw new InvalidOperationException("SF-PROD-AD-001: Active Directory server, search base and staff authorization groups must be configured.");
    if (!hasExternalProvider)
        throw new InvalidOperationException("SF-PROD-OAUTH-001: At least one external representative identity provider must be commissioned.");
    if (!notificationsEnabled || string.IsNullOrWhiteSpace(configuration["Notifications:SmtpHost"]) || string.IsNullOrWhiteSpace(configuration["Notifications:FromAddress"]))
        throw new InvalidOperationException("SF-PROD-SMTP-001: SMTP notifications must be commissioned for production.");
    if (string.IsNullOrWhiteSpace(configuration["Messaging:DurableQueuePath"]))
        throw new InvalidOperationException("SF-PROD-MESSAGING-001: Common.Messaging durable queue path must be configured in production.");
}

static async Task ApplyProductionMigrationsAsync(WebApplication app)
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    RequestPortalDbContext db = scope.ServiceProvider.GetRequiredService<RequestPortalDbContext>();
    string[] migrations = db.Database.GetMigrations().ToArray();
    if (migrations.Length == 0)
        throw new InvalidOperationException("SF-PROD-MIGRATION-001: No EF Core migrations exist. Production startup is blocked until a baseline migration is generated and reviewed.");
    await db.Database.MigrateAsync();
}

static async Task PrepareDemoAsync(WebApplication app)
{
    if (!app.Configuration.GetValue("Demo:Enabled", false)) return;
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>().InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
    await scope.ServiceProvider.GetRequiredService<DemoReadinessValidator>().ValidateAsync();
}

static async Task ReportStartupDiagnosticsAsync(WebApplication app)
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    IReadOnlyList<DiagnosticCheckResult> results = await scope.ServiceProvider.GetRequiredService<StartupDiagnosticRunner>().RunAsync();
    DiagnosticCheckResult? databaseResult = results.FirstOrDefault(result => result.Name == "PostgreSQL");
    if (databaseResult is not null && !databaseResult.IsHealthy)
        throw new InvalidOperationException($"SF-STARTUP-DB-001: PostgreSQL startup diagnostic failed. {databaseResult.Detail}");
}

public partial class Program { }
