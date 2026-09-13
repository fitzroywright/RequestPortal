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
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    ?? throw new InvalidOperationException("RP-CONFIG-DB-001: PostgreSQL connection string 'RequestPortal' is not configured.");

long uploadMaxBytes = builder.Configuration.GetValue<long?>("Uploads:MaxBytes") ?? 25L * 1024L * 1024L;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = uploadMaxBytes + (1024L * 1024L));
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = uploadMaxBytes);

builder.Services.AddDbContext<RequestPortalDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    if (builder.Environment.IsEnvironment("Demo"))
    {
        // Demo bootstrap and ASP.NET Identity perform a number of unique-key First/Single
        // lookups. EF translates those to LIMIT without ORDER BY and logs a warning even
        // though the predicate is deterministic. Suppress only this warning in Demo mode;
        // all other EF warnings remain enabled and production behavior is unchanged.
        options.ConfigureWarnings(warnings =>
            warnings.Ignore(CoreEventId.RowLimitingOperationWithoutOrderByWarning));
    }
});

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
builder.Services.AddOptions<StaffOidcOptions>().Bind(builder.Configuration.GetSection(StaffOidcOptions.SectionName));
builder.Services.AddOptions<NotificationOptions>().Bind(builder.Configuration.GetSection(NotificationOptions.SectionName));
builder.Services.AddOptions<ManagementDashboardOptions>().Bind(builder.Configuration.GetSection(ManagementDashboardOptions.SectionName));

StaffOidcOptions staffOidc = builder.Configuration
    .GetSection(StaffOidcOptions.SectionName)
    .Get<StaffOidcOptions>() ?? new StaffOidcOptions();
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
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                if (context.Principal is null)
                {
                    context.Fail("The staff identity provider returned no principal.");
                    return;
                }

                StaffOidcPrincipalService service = context.HttpContext.RequestServices
                    .GetRequiredService<StaffOidcPrincipalService>();

                StaffOidcPrincipalResult result = await service.BuildPrincipalAsync(
                    context.Principal,
                    context.HttpContext.RequestAborted);

                if (!result.Allowed || result.Principal is null)
                {
                    context.Fail(result.FailureCode ?? "Staff access denied.");
                    return;
                }

                context.Principal = result.Principal;
            }
        };
    });
}

// Remainder of Program.cs is unchanged on this branch and retained below.
