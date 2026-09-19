using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
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
using RequestPortal.Application.Management;
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
using RequestPortal.Common.Security;
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
using RequestPortal.Infrastructure.Management;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Notifications;
using RequestPortal.Infrastructure.Organizations;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Portal;
using RequestPortal.Infrastructure.ReferenceData;
using RequestPortal.Infrastructure.Reporting;
using RequestPortal.Infrastructure.Requests;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web;
using RequestPortal.Web.Demo;
using RequestPortal.Web.Security;
using RequestPortal.Web.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddHostedService<RequestPortal.Web.OperationsTelemetryPublisher>();
builder.Services.AddSingleton<RequestPortalOperationsFlowPublisher>();
builder.Services.AddScoped<IPortalWorkflowService, AuditedPortalWorkflowService>();
builder.Services.AddSingleton<IRequestPortalOperationsFlowPublisher>(sp => sp.GetRequiredService<RequestPortalOperationsFlowPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RequestPortalOperationsFlowPublisher>());
await using Common.Secrets.CommonSecretsBootstrapRuntime secretsRuntime = Common.Secrets.CommonSecretsBootstrapRuntime.Create(builder.Configuration);
RuntimeSecrets runtimeSecrets = await LoadRuntimeSecretsAsync(secretsRuntime.Provider);

bool registrationSucceeded = await ConfigurationContractPublisher.PublishBootstrapAsync(
    builder.Configuration,
    builder.Environment,
    secretsRuntime.Provider,
    CancellationToken.None);
if (!registrationSucceeded)
{
    Console.Error.WriteLine("RP-CONFIG-REG-001: RequestPortal could not publish its commissioning state to Aegis.Configuration before startup validation completed.");
}

if (builder.Environment.IsProduction())
{
    IReadOnlyList<ProductionConfigurationFailure> failures = ValidateProductionConfiguration(builder.Configuration, runtimeSecrets);
    if (failures.Count > 0)
    {
        throw new InvalidOperationException(BuildProductionValidationMessage(failures));
    }
}

string connectionString = runtimeSecrets.ConnectionString
    ?? throw new InvalidOperationException("ConnectionStrings:RequestPortal is required through Common.Secrets.");

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
authentication.AddCookie(StaffOidcOptions.StaffCookieScheme, options =>
{
    options.LoginPath = "/Account/StaffLogin";
    options.AccessDeniedPath = "/Account/StaffAccessDenied";
    options.Cookie.Name = "RequestPortal.FfpStaff";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.SlidingExpiration = false;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});

string? googleClientId = runtimeSecrets.GoogleClientId;
string? googleClientSecret = runtimeSecrets.GoogleClientSecret;
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    authentication.AddGoogle(options => { options.ClientId = googleClientId; options.ClientSecret = googleClientSecret; });
}

string? microsoftClientId = runtimeSecrets.MicrosoftClientId;
string? microsoftClientSecret = runtimeSecrets.MicrosoftClientSecret;
if (!string.IsNullOrWhiteSpace(microsoftClientId) && !string.IsNullOrWhiteSpace(microsoftClientSecret))
{
    authentication.AddMicrosoftAccount(options => { options.ClientId = microsoftClientId; options.ClientSecret = microsoftClientSecret; });
}

string? facebookAppId = runtimeSecrets.FacebookAppId;
string? facebookAppSecret = runtimeSecrets.FacebookAppSecret;
if (!string.IsNullOrWhiteSpace(facebookAppId) && !string.IsNullOrWhiteSpace(facebookAppSecret))
{
    authentication.AddFacebook(options => { options.AppId = facebookAppId; options.AppSecret = facebookAppSecret; });
}

builder.Services.AddOptions<ActiveDirectoryOptions>().Bind(builder.Configuration.GetSection(ActiveDirectoryOptions.SectionName));
builder.Services.AddOptions<StaffOidcOptions>().Bind(builder.Configuration.GetSection(StaffOidcOptions.SectionName));
builder.Services.AddOptions<NotificationOptions>().Bind(builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.AddOptions<ManagementDashboardOptions>().Bind(builder.Configuration.GetSection(ManagementDashboardOptions.SectionName));

StaffOidcOptions staffOidc = builder.Configuration
    .GetSection(StaffOidcOptions.SectionName)
    .Get<StaffOidcOptions>() ?? new StaffOidcOptions();
staffOidc.ClientSecret = runtimeSecrets.StaffOidcClientSecret;
if (staffOidc.Enabled)
{
    authentication.AddOpenIdConnect(StaffOidcOptions.Scheme, options =>
    {
        options.SignInScheme = StaffOidcOptions.StaffCookieScheme;
        options.Authority = staffOidc.Authority;
        options.ClientId = staffOidc.ClientId;
        options.ClientSecret = staffOidc.ClientSecret;
        options.CallbackPath = staffOidc.CallbackPath;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.RequireHttpsMetadata = staffOidc.RequireHttpsMetadata;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.Prompt = "login";
                string correlationId = Guid.NewGuid().ToString("N");
                context.Properties.Items["aegis.auth.correlation"] = correlationId;
                RequestPortalOperationsFlowPublisher flows = context.HttpContext.RequestServices
                    .GetRequiredService<RequestPortalOperationsFlowPublisher>();
                flows.TryEnqueueAuthentication(correlationId, "IdentityProvider");
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                string correlationId =
                    context.Properties?.Items.TryGetValue("aegis.auth.correlation", out string? value) == true &&
                    !string.IsNullOrWhiteSpace(value)
                        ? value
                        : Guid.NewGuid().ToString("N");
                RequestPortalOperationsFlowPublisher flows = context.HttpContext.RequestServices
                    .GetRequiredService<RequestPortalOperationsFlowPublisher>();

                if (context.Principal is null)
                {
                    flows.TryEnqueueAuthentication(
                        correlationId,
                        "Callback",
                        "Identity provider callback returned no principal.",
                        failed: true);
                    context.Fail("The staff identity provider returned no principal.");
                    return;
                }

                flows.TryEnqueueAuthentication(correlationId, "Claims");

                StaffOidcPrincipalService service = context.HttpContext.RequestServices
                    .GetRequiredService<StaffOidcPrincipalService>();
                StaffOidcPrincipalResult result = await service.BuildPrincipalAsync(
                    context.Principal,
                    context.HttpContext.RequestAborted);
                if (!result.Allowed || result.Principal is null)
                {
                    flows.TryEnqueueAuthentication(
                        correlationId,
                        "Authorization",
                        result.FailureCode ?? "Staff authorization denied.",
                        failed: true);
                    context.Fail(result.FailureCode ?? "FFP staff access denied.");
                    return;
                }

                context.Principal = result.Principal;
                flows.TryEnqueueAuthentication(correlationId, "Session");
            },
            OnRemoteFailure = context =>
            {
                string correlationId =
                    context.Properties?.Items.TryGetValue("aegis.auth.correlation", out string? value) == true &&
                    !string.IsNullOrWhiteSpace(value)
                        ? value
                        : Guid.NewGuid().ToString("N");
                RequestPortalOperationsFlowPublisher flows = context.HttpContext.RequestServices
                    .GetRequiredService<RequestPortalOperationsFlowPublisher>();
                flows.TryEnqueueAuthentication(
                    correlationId,
                    "Callback",
                    "Remote authentication failed before a session was established.",
                    failed: true);
                context.HandleResponse();
                context.Response.Redirect("/Account/StaffAccessDenied");
                return Task.CompletedTask;
            }
        };
    });
}

Common.Security.Integration.SecurityServiceCollectionExtensions.AddCommonAuthorization<RequestPortalAuthorizationStore>(builder.Services);
Common.Security.Integration.SecurityServiceCollectionExtensions.AddCommonSecurityDiagnostics(builder.Services);
secretsRuntime.Register(builder.Services);
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
if (builder.Configuration.GetValue("Messaging:MicrosoftGraph:Email:Enabled", false))
{
    Common.Messaging.Hosting.ChannelServiceCollectionExtensions.AddMicrosoftGraphEmailMessagingChannel(
        builder.Services,
        new Common.Messaging.Channels.MicrosoftGraph.MicrosoftGraphEmailOptions
        {
            TenantId = builder.Configuration["Messaging:MicrosoftGraph:TenantId"] ?? string.Empty,
            ClientId = builder.Configuration["Messaging:MicrosoftGraph:ClientId"] ?? string.Empty,
            SenderUpn = builder.Configuration["Messaging:MicrosoftGraph:Email:SenderUpn"] ?? string.Empty,
            ClientSecretName = builder.Configuration["Messaging:MicrosoftGraph:ClientSecretName"]
                ?? "messaging/msgraph/client-secret"
        });
}
if (builder.Configuration.GetValue("Messaging:MicrosoftGraph:Teams:Enabled", false))
{
    Common.Messaging.Hosting.ChannelServiceCollectionExtensions.AddMicrosoftGraphTeamsMessagingChannel(
        builder.Services,
        new Common.Messaging.Channels.MicrosoftGraph.MicrosoftGraphTeamsOptions
        {
            SenderUpn = builder.Configuration["Messaging:MicrosoftGraph:Teams:SenderUpn"] ?? string.Empty,
            DelegatedAccessTokenSecretName = builder.Configuration["Messaging:MicrosoftGraph:Teams:DelegatedAccessTokenSecretName"]
                ?? "messaging/msgraph/teams/delegated-access-token"
        });
}
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
if (notificationOptions.SmsEnabled)
{
    Common.Messaging.Hosting.ChannelServiceCollectionExtensions.AddSmsMessagingChannel(
        builder.Services,
        new Common.Messaging.Channels.Sms.SmsMessageOptions
        {
            Endpoint = notificationOptions.SmsEndpoint,
            EndpointSecretName = notificationOptions.SmsEndpointSecretName,
            ApiTokenSecretName = notificationOptions.SmsApiTokenSecretName
        });
}
if (notificationOptions.WhatsAppEnabled)
{
    Common.Messaging.Hosting.ChannelServiceCollectionExtensions.AddWhatsAppMessagingChannel(
        builder.Services,
        new Common.Messaging.Channels.WhatsApp.WhatsAppMessageOptions
        {
            Endpoint = notificationOptions.WhatsAppEndpoint,
            EndpointSecretName = notificationOptions.WhatsAppEndpointSecretName,
            ApiTokenSecretName = notificationOptions.WhatsAppApiTokenSecretName
        });
}
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
builder.Services.AddScoped<IStaffOrganizationDirectoryService, StaffOrganizationDirectoryService>();
builder.Services.AddScoped<IStaffOrganizationService, StaffOrganizationService>();
builder.Services.AddScoped<IStaffOperationsService, StaffOperationsService>();
builder.Services.AddScoped<IStaffReportingService, StaffReportingService>();
builder.Services.AddScoped<IManagementDashboardService, ManagementDashboardService>();
builder.Services.AddScoped<IRequestLocationService, RequestLocationService>();
builder.Services.AddScoped<IBusinessAuditWriter, BusinessAuditWriter>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IBeneficiaryService, BeneficiaryService>();
builder.Services.AddScoped<IRequestDraftService, RequestDraftService>();
builder.Services.AddScoped<IProjectProposalService, ProjectProposalService>();
builder.Services.AddScoped<IProjectPlanningService, ProjectPlanningService>();
builder.Services.AddScoped<IRequestLifecycleService, RequestLifecycleService>();
builder.Services.AddScoped<IFulfilmentQueueService, FulfilmentQueueService>();
builder.Services.AddScoped<IIntegrationStatusService, IntegrationStatusService>();
builder.Services.AddFfpManagerIntegration(builder.Configuration);
builder.Services.AddScoped<StaffOidcPrincipalService>();
builder.Services.AddScoped<OutboxDispatcher>();
builder.Services.AddHostedService<OutboxHostedService>();
builder.Services.AddHostedService<ConfigurationContractPublisher>();
builder.Services.AddScoped<DemoDatabaseBootstrapper>();
builder.Services.AddScoped<DemoDataSeeder>();
builder.Services.AddScoped<DemoReadinessValidator>();
builder.Services.AddEngineeringDiagnostics();
builder.Services.AddScoped<IStartupDiagnostic, PostgreSqlStartupDiagnostic>();
builder.Services.AddScoped<StartupDiagnosticRunner>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(PortalPolicies.FfpStaff, policy =>
    {
        policy.AuthenticationSchemes.Add(StaffOidcOptions.StaffCookieScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);
        policy.RequireClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access);
    });
    options.AddPolicy(PortalPolicies.ManagementDashboard, policy =>
    {
        policy.AuthenticationSchemes.Add(StaffOidcOptions.StaffCookieScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);
        policy.RequireClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ViewManagementDashboard);
    });
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Dashboard");
    options.Conventions.AuthorizeFolder("/Staff", PortalPolicies.FfpStaff);
    options.Conventions.AuthorizeFolder("/Management", PortalPolicies.ManagementDashboard);
    options.Conventions.AuthorizeFolder("/Organizations");
    options.Conventions.AuthorizeFolder("/Requests");
});

WebApplication app = builder.Build();

await ApplyDatabaseMigrationsAsync(app);

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
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(self)";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapFfpManagerIntegrationEndpoints();

await PrepareDemoAsync(app);
await ReportStartupDiagnosticsAsync(app);
await app.RunAsync();

static async Task<RuntimeSecrets> LoadRuntimeSecretsAsync(Common.Secrets.ISecretProvider provider)
{
    return new RuntimeSecrets(
        await provider.GetAsync("ConnectionStrings:RequestPortal"),
        await provider.GetAsync("Authentication:Google:ClientId"),
        await provider.GetAsync("Authentication:Google:ClientSecret"),
        await provider.GetAsync("Authentication:Microsoft:ClientId"),
        await provider.GetAsync("Authentication:Microsoft:ClientSecret"),
        await provider.GetAsync("Authentication:Facebook:AppId"),
        await provider.GetAsync("Authentication:Facebook:AppSecret"),
        await provider.GetAsync("Authentication:StaffOidc:ClientSecret"),
        await provider.GetAsync("FfpManagerIntegration:SharedSecret"));
}

static IReadOnlyList<ProductionConfigurationFailure> ValidateProductionConfiguration(
    IConfiguration configuration,
    RuntimeSecrets secrets)
{
    var failures = new List<ProductionConfigurationFailure>();
    string connectionString = secrets.ConnectionString ?? string.Empty;
    string storageRoot = configuration["Storage:Local:RootPath"] ?? string.Empty;
    string keyPath = configuration["Security:DataProtectionKeysPath"] ?? string.Empty;
    string allowedHosts = configuration["AllowedHosts"] ?? string.Empty;
    bool malwareScanningEnabled = configuration.GetValue("Uploads:MalwareScanning:Enabled", false);
    bool notificationsEnabled = configuration.GetValue("Notifications:Enabled", false);
    bool ffpManagerEnabled = configuration.GetValue("FfpManagerIntegration:Enabled", false);
    string[] reviewerGroups = configuration.GetSection("Authentication:ActiveDirectory:ReviewerGroups").Get<string[]>() ?? Array.Empty<string>();
    string[] administratorGroups = configuration.GetSection("Authentication:ActiveDirectory:AdministratorGroups").Get<string[]>() ?? Array.Empty<string>();
    string? googleClientId = secrets.GoogleClientId;
    string? googleClientSecret = secrets.GoogleClientSecret;
    string? microsoftClientId = secrets.MicrosoftClientId;
    string? microsoftClientSecret = secrets.MicrosoftClientSecret;
    string? facebookAppId = secrets.FacebookAppId;
    string? facebookAppSecret = secrets.FacebookAppSecret;
    string? staffOidcClientSecret = secrets.StaffOidcClientSecret;
    string? ffpManagerSharedSecret = secrets.FfpManagerSharedSecret;
    bool hasExternalProvider =
        (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret)) ||
        (!string.IsNullOrWhiteSpace(microsoftClientId) && !string.IsNullOrWhiteSpace(microsoftClientSecret)) ||
        (!string.IsNullOrWhiteSpace(facebookAppId) && !string.IsNullOrWhiteSpace(facebookAppSecret));
    bool staffOidcEnabled = configuration.GetValue("Authentication:StaffOidc:Enabled", false);

    AddIf(string.IsNullOrWhiteSpace(connectionString),
        "RP-CONFIG-DB-001",
        "PostgreSQL connection string 'RequestPortal' is not configured through Common.Secrets.");
    AddIf(string.IsNullOrWhiteSpace(storageRoot) || storageRoot.Contains("App_Data", StringComparison.OrdinalIgnoreCase),
        "RP-PROD-STORAGE-001",
        "Production document storage must be a durable external filesystem path or network share.");
    AddIf(string.IsNullOrWhiteSpace(keyPath) || keyPath.Contains("App_Data", StringComparison.OrdinalIgnoreCase),
        "RP-PROD-DP-001",
        "Production Data Protection keys must be persisted outside the application deployment directory.");
    AddIf(allowedHosts == "*" || string.IsNullOrWhiteSpace(allowedHosts),
        "RP-PROD-HOSTS-001",
        "AllowedHosts must be restricted in production.");
    AddIf(!malwareScanningEnabled || string.IsNullOrWhiteSpace(configuration["Uploads:MalwareScanning:Host"]),
        "RP-PROD-UPLOAD-001",
        "Malware scanning must be configured and enabled in production.");
    AddIf(reviewerGroups.Length == 0 || administratorGroups.Length == 0,
        "RP-PROD-STAFF-GROUPS-001",
        "FFP staff directory-group mappings must be configured for Common.Security.");
    AddIf(!staffOidcEnabled ||
          string.IsNullOrWhiteSpace(configuration["Authentication:StaffOidc:Authority"]) ||
          string.IsNullOrWhiteSpace(configuration["Authentication:StaffOidc:ClientId"]) ||
          string.IsNullOrWhiteSpace(staffOidcClientSecret),
        "RP-PROD-OIDC-001",
        "FFP staff OIDC Authority, ClientId and Common.Secrets-backed ClientSecret must be commissioned.");
    AddIf(!hasExternalProvider,
        "RP-PROD-OAUTH-001",
        "At least one external representative identity provider must be commissioned.");
    AddIf(!notificationsEnabled || string.IsNullOrWhiteSpace(configuration["Notifications:SmtpHost"]) || string.IsNullOrWhiteSpace(configuration["Notifications:FromAddress"]),
        "RP-PROD-SMTP-001",
        "SMTP notifications must be commissioned for production.");
    AddIf(string.IsNullOrWhiteSpace(configuration["Messaging:DurableQueuePath"]),
        "RP-PROD-MESSAGING-001",
        "Common.Messaging durable queue path must be configured in production.");
    AddIf(string.IsNullOrWhiteSpace(configuration["Site:Id"]),
        "RP-PROD-SITE-001",
        "Site:Id must identify the production deployment.");
    AddIf(ffpManagerEnabled &&
          (string.IsNullOrWhiteSpace(configuration["FfpManagerIntegration:BaseUrl"]) ||
           string.IsNullOrWhiteSpace(ffpManagerSharedSecret)),
        "RP-PROD-FFPMANAGER-001",
        "FFP Manager integration requires BaseUrl and a Common.Secrets-backed SharedSecret when enabled.");

    return failures;

    void AddIf(bool condition, string code, string message)
    {
        if (condition)
        {
            failures.Add(new ProductionConfigurationFailure(code, message));
        }
    }
}

static string BuildProductionValidationMessage(IReadOnlyList<ProductionConfigurationFailure> failures)
{
    return "RequestPortal production commissioning failed with " + failures.Count + " issue(s):\n"
        + string.Join("\n", failures.Select(failure => $"{failure.Code}: {failure.Message}"));
}

static async Task ApplyDatabaseMigrationsAsync(WebApplication app)
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    RequestPortalDbContext db = scope.ServiceProvider.GetRequiredService<RequestPortalDbContext>();
    string[] migrations = db.Database.GetMigrations().ToArray();
    if (migrations.Length == 0)
        throw new InvalidOperationException("RP-MIGRATION-001: No EF Core migrations exist. Startup is blocked until a baseline migration is generated and reviewed.");
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
        throw new InvalidOperationException($"RP-STARTUP-DB-001: PostgreSQL startup diagnostic failed. {databaseResult.Detail}");
}

internal sealed record RuntimeSecrets(
    string? ConnectionString,
    string? GoogleClientId,
    string? GoogleClientSecret,
    string? MicrosoftClientId,
    string? MicrosoftClientSecret,
    string? FacebookAppId,
    string? FacebookAppSecret,
    string? StaffOidcClientSecret,
    string? FfpManagerSharedSecret);

internal sealed record ProductionConfigurationFailure(string Code, string Message);

public partial class Program { }
