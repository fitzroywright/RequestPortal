using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
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
using RequestPortal.Application.Reporting;
using RequestPortal.Application.Requests;
using RequestPortal.Application.Security;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Secrets;
using RequestPortal.Common.Security;
using RequestPortal.Common.Storage;
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
using RequestPortal.Infrastructure.Reporting;
using RequestPortal.Infrastructure.Requests;
using RequestPortal.Infrastructure.Secrets;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Infrastructure.Storage;
using RequestPortal.Web.Demo;
using RequestPortal.Web.Security;
using RequestPortal.Web.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("RequestPortal")
    ?? throw new InvalidOperationException(
        "SF-CONFIG-DB-001: PostgreSQL connection string 'RequestPortal' is not configured.");

builder.Services.AddDbContext<RequestPortalDbContext>(options =>
    options.UseNpgsql(connectionString));

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
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
});

AuthenticationBuilder authentication = builder.Services.AddAuthentication();

string? googleClientId = builder.Configuration["Authentication:Google:ClientId"];
string? googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    authentication.AddGoogle(options =>
    {
        options.ClientId = googleClientId;
        options.ClientSecret = googleClientSecret;
    });
}

string? microsoftClientId = builder.Configuration["Authentication:Microsoft:ClientId"];
string? microsoftClientSecret = builder.Configuration["Authentication:Microsoft:ClientSecret"];
if (!string.IsNullOrWhiteSpace(microsoftClientId) && !string.IsNullOrWhiteSpace(microsoftClientSecret))
{
    authentication.AddMicrosoftAccount(options =>
    {
        options.ClientId = microsoftClientId;
        options.ClientSecret = microsoftClientSecret;
    });
}

string? facebookAppId = builder.Configuration["Authentication:Facebook:AppId"];
string? facebookAppSecret = builder.Configuration["Authentication:Facebook:AppSecret"];
if (!string.IsNullOrWhiteSpace(facebookAppId) && !string.IsNullOrWhiteSpace(facebookAppSecret))
{
    authentication.AddFacebook(options =>
    {
        options.AppId = facebookAppId;
        options.AppSecret = facebookAppSecret;
    });
}

builder.Services
    .AddOptions<ActiveDirectoryOptions>()
    .Bind(builder.Configuration.GetSection(ActiveDirectoryOptions.SectionName));

builder.Services
    .AddOptions<NotificationOptions>()
    .Bind(builder.Configuration.GetSection(NotificationOptions.SectionName));

builder.Services.AddSingleton<IClock, RequestPortal.Common.Time.SystemClock>();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
builder.Services.AddSingleton<ISecretProvider, ConfigurationSecretProvider>();

builder.Services.AddScoped<INotificationSender, SmtpNotificationSender>();
builder.Services.AddScoped<INotificationPreferenceService, NotificationPreferenceService>();
builder.Services.AddScoped<ICountryPolicyService, CountryPolicyService>();
builder.Services.AddScoped<IOrganizationMembershipGovernanceService, OrganizationMembershipGovernanceService>();
builder.Services.AddScoped<IStaffDirectoryAuthenticator, ActiveDirectoryStaffAuthenticator>();
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

builder.Services.AddScoped<DemoDatabaseBootstrapper>();
builder.Services.AddScoped<DemoDataSeeder>();
builder.Services.AddScoped<DemoReadinessValidator>();

builder.Services.AddStarfleetDiagnostics();
builder.Services.AddScoped<IStartupDiagnostic, PostgreSqlStartupDiagnostic>();
builder.Services.AddScoped<IStartupDiagnostic, ActiveDirectoryStartupDiagnostic>();
builder.Services.AddScoped<StartupDiagnosticRunner>();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        PortalPolicies.FfpStaff,
        policy => policy.RequireRole(PortalRoles.FfpAdministrator, PortalRoles.FfpReviewer));
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Dashboard");
    options.Conventions.AuthorizeFolder("/Staff", PortalPolicies.FfpStaff);
    options.Conventions.AuthorizeFolder("/Organizations");
    options.Conventions.AuthorizeFolder("/Requests");
});

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

await PrepareDemoAsync(app);
await ReportStartupDiagnosticsAsync(app);
await app.RunAsync();

static async Task PrepareDemoAsync(WebApplication app)
{
    if (!app.Configuration.GetValue("Demo:Enabled", false))
    {
        return;
    }

    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    DemoDatabaseBootstrapper bootstrapper = scope.ServiceProvider.GetRequiredService<DemoDatabaseBootstrapper>();
    DemoDataSeeder seeder = scope.ServiceProvider.GetRequiredService<DemoDataSeeder>();
    DemoReadinessValidator validator = scope.ServiceProvider.GetRequiredService<DemoReadinessValidator>();

    await bootstrapper.InitializeAsync();
    await seeder.SeedAsync();
    await validator.ValidateAsync();
}

static async Task ReportStartupDiagnosticsAsync(WebApplication app)
{
    await using AsyncServiceScope scope = app.Services.CreateAsyncScope();
    StartupDiagnosticRunner runner = scope.ServiceProvider.GetRequiredService<StartupDiagnosticRunner>();
    IReadOnlyList<DiagnosticCheckResult> results = await runner.RunAsync();

    DiagnosticCheckResult? databaseResult = results.FirstOrDefault(result => result.Name == "PostgreSQL");
    if (databaseResult is not null && !databaseResult.IsHealthy)
    {
        throw new InvalidOperationException(
            $"SF-STARTUP-DB-001: PostgreSQL startup diagnostic failed. {databaseResult.Detail}");
    }
}

public partial class Program
{
}
