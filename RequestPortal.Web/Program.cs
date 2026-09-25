using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
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
using Common.Diagnostics;
using Common.Registration;
using Common.Secrets;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Beneficiaries;
using RequestPortal.Application.Countries;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Delivery;
using RequestPortal.Application.Fulfilment;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Management;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Notifications;
using RequestPortal.Application.Organizations;
using RequestPortal.Application.Portal;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Reviewers;
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
using RequestPortal.Infrastructure.Delivery;
using RequestPortal.Infrastructure.Fulfilment;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Management;
using RequestPortal.Infrastructure.Messages;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Notifications;
using RequestPortal.Infrastructure.Organizations;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Portal;
using RequestPortal.Infrastructure.ReferenceData;
using RequestPortal.Infrastructure.Reviewers;
using RequestPortal.Infrastructure.Reporting;
using RequestPortal.Infrastructure.Requests;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web;
using RequestPortal.Web.Configuration;
using RequestPortal.Web.Demo;
using RequestPortal.Web.Diagnostics;
using RequestPortal.Web.Messaging;
using RequestPortal.Web.Security;
using RequestPortal.Web.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
string operationsLogInstanceId = builder.Configuration["Service:Identity"]?.Trim() ?? Environment.MachineName;
string operationsLogIdentityFile = builder.Configuration["Aegis:Registration:IdentityFile"]
    ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Aegis", "RequestPortal", "registration-identity.json")
        : "/var/lib/aegis/requestportal/registration-identity.json");
builder.Services.AddAegisOperationsLogging(builder.Configuration, "RequestPortal", operationsLogInstanceId, operationsLogIdentityFile, builder.Environment.EnvironmentName);
builder.Services.AddHostedService<RequestPortal.Web.OperationsTelemetryPublisher>();
builder.Services.AddSingleton<RequestPortalOperationsFlowPublisher>();
builder.Services.AddScoped<IPortalWorkflowService, AuditedPortalWorkflowService>();
builder.Services.AddSingleton<IRequestPortalOperationsFlowPublisher>(sp => sp.GetRequiredService<RequestPortalOperationsFlowPublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RequestPortalOperationsFlowPublisher>());
await using Common.Secrets.CommonSecretsBootstrapRuntime secretsRuntime = Common.Secrets.CommonSecretsBootstrapRuntime.Create(builder.Configuration);
secretsRuntime.Register(builder.Services);
RuntimeSecrets runtimeSecrets = await ProductionCommissioning.LoadRuntimeSecretsAsync(
    secretsRuntime.Provider,
    builder.Configuration);

builder.Services.AddCommonDiagnostics();
builder.Services.AddSingleton(new DiagnosticLevelExecutionOptions
{
    Application = "RequestPortal",
    Component = "RequestPortal.Web",
    InstanceId = operationsLogInstanceId,
    CatalogVersion = "1"
});
builder.Services.AddSingleton(new DiagnosticLevelStoreOptions
{
    Path = builder.Configuration["Aegis:Diagnostics:StorePath"]?.Trim()
        ?? Path.Combine(builder.Environment.ContentRootPath, "data", "diagnostic-level", "requestportal.db")
});
builder.Services.AddSingleton<IDiagnosticLevelRequestCredentialProvider, CommonSecretsDiagnosticLevelRequestCredentialProvider>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, BasicRuntimeIdentityDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, BasicControlPlaneConfigurationDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, PostgreSqlConnectivityDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, MalwareScannerConnectivityDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, ExternalIdentityProvidersDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, OrganizationAdministratorIntegrityDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, MembershipStateIntegrityDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, RequestRelationshipIntegrityDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, DocumentStorageReferenceDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, ExpiredOrganizationRegistrationDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, FulfilmentIntegrityDiagnosticLevelTest>();
builder.Services.AddSingleton<IDiagnosticLevelLocalTest, FfpManagerBridgeReadinessDiagnosticLevelTest>();

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
    IReadOnlyList<ProductionConfigurationFailure> failures = ProductionCommissioning.Validate(builder.Configuration, runtimeSecrets);
    if (failures.Count > 0)
    {
        throw new InvalidOperationException(ProductionCommissioning.BuildFailureMessage(failures));
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

authentication.AddCookie("ExternalReviewer", options =>
{
    options.LoginPath = "/ExternalReview/AccessDenied";
    options.AccessDeniedPath = "/ExternalReview/AccessDenied";
    options.Cookie.Name = "RequestPortal.ExternalReview";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.SlidingExpiration = false;
    options.ExpireTimeSpan = TimeSpan.FromHours(4);
});

string? googleClientId = builder.Configuration["Authentication:Google:ClientId"];
string? googleClientSecret = runtimeSecrets.GoogleClientSecret;
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    authentication.AddGoogle(options => { options.ClientId = googleClientId; options.ClientSecret = googleClientSecret; });
}

string? microsoftClientId = builder.Configuration["Authentication:Microsoft:ClientId"];
string? microsoftClientSecret = runtimeSecrets.MicrosoftClientSecret;
if (!string.IsNullOrWhiteSpace(microsoftClientId) && !string.IsNullOrWhiteSpace(microsoftClientSecret))
{
    authentication.AddMicrosoftAccount(options => { options.ClientId = microsoftClientId; options.ClientSecret = microsoftClientSecret; });
}

string? facebookAppId = builder.Configuration["Authentication:Facebook:AppId"];
string? facebookAppSecret = runtimeSecrets.FacebookAppSecret;
if (!string.IsNullOrWhiteSpace(facebookAppId) && !string.IsNullOrWhiteSpace(facebookAppSecret))
{
    authentication.AddFacebook(options => { options.AppId = facebookAppId; options.AppSecret = facebookAppSecret; });
}

string? yahooClientId = builder.Configuration["Authentication:Yahoo:ClientId"];
string? yahooClientSecret = runtimeSecrets.YahooClientSecret;
if (!string.IsNullOrWhiteSpace(yahooClientId) && !string.IsNullOrWhiteSpace(yahooClientSecret))
{
    authentication.AddOpenIdConnect("Yahoo", options =>
    {
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.Authority = "https://api.login.yahoo.com";
        options.MetadataAddress = "https://api.login.yahoo.com/.well-known/openid-configuration";
        options.ClientId = yahooClientId;
        options.ClientSecret = yahooClientSecret;
        options.CallbackPath = "/signin-yahoo";
        options.ResponseType = "code";
        options.UsePkce = true;
        options.RequireHttpsMetadata = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.ClaimActions.MapUniqueJsonKey(ClaimTypes.NameIdentifier, "sub");
        options.ClaimActions.MapUniqueJsonKey(ClaimTypes.Name, "name");
        options.ClaimActions.MapUniqueJsonKey(ClaimTypes.Email, "email");
    });
}

string? appleClientId = builder.Configuration["Authentication:Apple:ClientId"];
string? appleClientSecret = runtimeSecrets.AppleClientSecret;
if (!string.IsNullOrWhiteSpace(appleClientId) && !string.IsNullOrWhiteSpace(appleClientSecret))
{
    authentication.AddOpenIdConnect("Apple", options =>
    {
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.Authority = "https://appleid.apple.com";
        options.MetadataAddress = "https://appleid.apple.com/.well-known/openid-configuration";
        options.ClientId = appleClientId;
        options.ClientSecret = appleClientSecret;
        options.CallbackPath = "/signin-apple";
        options.ResponseType = "code";
        options.ResponseMode = "form_post";
        options.RequireHttpsMetadata = true;
        options.SaveTokens = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("name");
        options.Scope.Add("email");
        options.ClaimActions.MapUniqueJsonKey(ClaimTypes.NameIdentifier, "sub");
        options.ClaimActions.MapUniqueJsonKey(ClaimTypes.Email, "email");
    });
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
builder.Services.AddSingleton<IMalwareScanner, ClamAvMalwareScanner>();
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

builder.Services.AddSignalR();
builder.Services.AddSingleton<IPortalMessageRealtimeNotifier, SignalRPortalMessageRealtimeNotifier>();
builder.Services.AddScoped<IPortalMessageService, PortalMessageService>();
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
builder.Services.AddScoped<IExternalReviewerService, ExternalReviewerService>();
builder.Services.AddScoped<IManagementDashboardService, ManagementDashboardService>();
builder.Services.AddScoped<IRequestLocationService, RequestLocationService>();
builder.Services.AddScoped<IBusinessAuditWriter, BusinessAuditWriter>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IDeliveryService, DeliveryService>();
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
    options.AddPolicy("ExternalReview", policy =>
    {
        policy.AuthenticationSchemes.Add("ExternalReviewer");
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("requestportal.external-review", "true");
        policy.RequireClaim("requestportal.external-review.scope");
    });
    options.AddPolicy("PortalMessages", policy =>
    {
        policy.AuthenticationSchemes.Add(IdentityConstants.ApplicationScheme);
        policy.AuthenticationSchemes.Add(StaffOidcOptions.StaffCookieScheme);
        policy.RequireAuthenticatedUser();
    });
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Dashboard");
    options.Conventions.AuthorizeFolder("/Staff", PortalPolicies.FfpStaff);
    options.Conventions.AuthorizePage("/Staff/Reviewers", PortalPolicies.ManagementDashboard);
    options.Conventions.AuthorizeFolder("/Management", PortalPolicies.ManagementDashboard);
    options.Conventions.AuthorizeFolder("/Organizations");
    options.Conventions.AuthorizeFolder("/Requests");
    options.Conventions.AuthorizeFolder("/Messages", "PortalMessages");
    options.Conventions.AuthorizePage("/ExternalReview/Index", "ExternalReview");
});

WebApplication app = builder.Build();
app.MapDiagnosticLevelRuntime();

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
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(self)";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapHub<PortalMessageHub>("/hubs/messages").RequireAuthorization("PortalMessages");
app.MapGet(
    "/api/messages/unread-count",
    async (
        ClaimsPrincipal user,
        IPortalMessageService messageService,
        HttpContext context) =>
    {
        string? userIdValue = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdValue, out Guid userId))
        {
            return Results.Unauthorized();
        }

        bool isStaff = user.HasClaim(
            StaffOidcOptions.StaffSurfaceClaim,
            StaffOidcOptions.StaffSurfaceValue);
        int unread = await messageService.GetUnreadCountAsync(
            userId,
            isStaff,
            context.RequestAborted);
        return Results.Ok(new { unread });
    })
    .RequireAuthorization("PortalMessages");
app.MapFfpManagerIntegrationEndpoints();
app.MapDeliveryEndpoints();

await PrepareDemoAsync(app);
await ReportStartupDiagnosticsAsync(app);
await app.RunAsync();

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


public partial class Program { }
