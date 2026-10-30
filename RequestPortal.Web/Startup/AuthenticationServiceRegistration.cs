using Microsoft.AspNetCore.Authentication;
using Common.Security.Hosting;
using Microsoft.AspNetCore.Identity;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Configuration;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Startup;

internal static class AuthenticationServiceRegistration
{
    public static void Add(
        WebApplicationBuilder builder
        , RuntimeSecrets runtimeSecrets)
    {
        AddIdentity(builder);
        AuthenticationBuilder authentication =
            builder.Services.AddAuthentication();

        authentication.AddPolicyScheme(PortalMessageAuthentication.Scheme,null,options=>
            options.ForwardDefaultSelector=PortalMessageAuthentication.SelectScheme);
        AddStaffCookie(authentication);
        AddExternalReviewerCookie(authentication);

        RepresentativeAuthenticationRegistration.Add(
            authentication
            , builder.Configuration
            , runtimeSecrets);

        builder.Services
            .AddOptions<ActiveDirectoryOptions>()
            .Bind(
                builder.Configuration.GetSection(
                    ActiveDirectoryOptions.SectionName));

        builder.Services
            .AddOptions<StaffOidcOptions>()
            .Bind(
                builder.Configuration.GetSection(
                    StaffOidcOptions.SectionName));

        StaffAuthenticationRegistration.Add(
            authentication
            , builder.Configuration
            , runtimeSecrets.StaffOidcClientSecret);
    }

    private static void AddIdentity(
        WebApplicationBuilder builder)
    {
        builder.Services
            .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<
                RequestPortal.Infrastructure.Persistence.RequestPortalDbContext>()
            .AddDefaultTokenProviders();

        builder.Services.ConfigureApplicationCookie(options =>
        {
            var stampValidation=options.Events.OnValidatePrincipal;
            options.Events.OnValidatePrincipal=async context =>
            {
                await stampValidation(context);
                if(context.Principal is null) return;
                await AccountSecurityHosting.ValidateCookieAsync(context,
                    context.HttpContext.RequestServices.GetRequiredService<RequestPortalAccounts>().Public);
            };
            options.LoginPath = "/Account/Login";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.Cookie.Name = "RequestPortal.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy =
                CookieSecurePolicy.Always;
            options.Cookie.SameSite =
                SameSiteMode.Lax;
            options.SlidingExpiration = true;
            options.ExpireTimeSpan =
                TimeSpan.FromHours(8);
        });
    }

    private static void AddStaffCookie(
        AuthenticationBuilder authentication)
    {
        authentication.AddCookie(
            StaffOidcOptions.StaffCookieScheme
            , options =>
            {
                options.Events.OnValidatePrincipal=context => context.HttpContext.RequestServices
                    .GetRequiredService<StaffOidcPrincipalService>().ValidateCookieAsync(context);
                options.LoginPath = "/Account/StaffLogin";
                options.AccessDeniedPath =
                    "/Account/StaffAccessDenied";
                options.Cookie.Name =
                    "RequestPortal.FfpStaff";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy =
                    CookieSecurePolicy.Always;
                options.Cookie.SameSite =
                    SameSiteMode.Lax;
                options.SlidingExpiration = false;
                options.ExpireTimeSpan =
                    TimeSpan.FromHours(8);
            });
    }

    private static void AddExternalReviewerCookie(
        AuthenticationBuilder authentication)
    {
        authentication.AddCookie(
            "ExternalReviewer"
            , options =>
            {
                options.LoginPath =
                    "/ExternalReview/AccessDenied";
                options.AccessDeniedPath =
                    "/ExternalReview/AccessDenied";
                options.Cookie.Name =
                    "RequestPortal.ExternalReview";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy =
                    CookieSecurePolicy.Always;
                options.Cookie.SameSite =
                    SameSiteMode.Lax;
                options.SlidingExpiration = false;
                options.ExpireTimeSpan =
                    TimeSpan.FromHours(4);
            });
    }
}
