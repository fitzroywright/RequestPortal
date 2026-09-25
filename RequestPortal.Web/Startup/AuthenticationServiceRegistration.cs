using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Identity;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Web.Configuration;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Startup;

internal static class AuthenticationServiceRegistration
{
    public static void Add(
        WebApplicationBuilder builder
        , RuntimeSecrets runtimeSecrets)
    {
        builder.Services
            .AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<RequestPortal.Infrastructure.Persistence.RequestPortalDbContext>()
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

        AuthenticationBuilder authentication =
            builder.Services.AddAuthentication();

        AddStaffCookie(authentication);
        AddExternalReviewerCookie(authentication);
        AddRepresentativeProviders(
            authentication
            , builder.Configuration
            , runtimeSecrets);

        builder.Services
            .AddOptions<ActiveDirectoryOptions>()
            .Bind(builder.Configuration.GetSection(ActiveDirectoryOptions.SectionName));
        builder.Services
            .AddOptions<StaffOidcOptions>()
            .Bind(builder.Configuration.GetSection(StaffOidcOptions.SectionName));

        AddStaffOidc(
            authentication
            , builder.Configuration
            , runtimeSecrets.StaffOidcClientSecret);
    }

    private static void AddStaffCookie(AuthenticationBuilder authentication)
    {
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
    }

    private static void AddExternalReviewerCookie(
        AuthenticationBuilder authentication)
    {
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
    }

    private static void AddRepresentativeProviders(
        AuthenticationBuilder authentication
        , IConfiguration configuration
        , RuntimeSecrets runtimeSecrets)
    {
        AddGoogle(
            authentication
            , configuration
            , runtimeSecrets.GoogleClientSecret);
        AddMicrosoft(
            authentication
            , configuration
            , runtimeSecrets.MicrosoftClientSecret);
        AddFacebook(
            authentication
            , configuration
            , runtimeSecrets.FacebookAppSecret);
        AddYahoo(
            authentication
            , configuration
            , runtimeSecrets.YahooClientSecret);
        AddApple(
            authentication
            , configuration
            , runtimeSecrets.AppleClientSecret);
    }

    private static void AddGoogle(
        AuthenticationBuilder authentication
        , IConfiguration configuration
        , string? clientSecret)
    {
        string? clientId = configuration["Authentication:Google:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return;
        }

        authentication.AddGoogle(options =>
        {
            options.ClientId = clientId;
            options.ClientSecret = clientSecret;
        });
    }

    private static void AddMicrosoft(
        AuthenticationBuilder authentication
        , IConfiguration configuration
        , string? clientSecret)
    {
        string? clientId = configuration["Authentication:Microsoft:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return;
        }

        authentication.AddMicrosoftAccount(options =>
        {
            options.ClientId = clientId;
            options.ClientSecret = clientSecret;
        });
    }

    private static void AddFacebook(
        AuthenticationBuilder authentication
        , IConfiguration configuration
        , string? appSecret)
    {
        string? appId = configuration["Authentication:Facebook:AppId"];
        if (string.IsNullOrWhiteSpace(appId) ||
            string.IsNullOrWhiteSpace(appSecret))
        {
            return;
        }

        authentication.AddFacebook(options =>
        {
            options.AppId = appId;
            options.AppSecret = appSecret;
        });
    }

    private static void AddYahoo(
        AuthenticationBuilder authentication
        , IConfiguration configuration
        , string? clientSecret)
    {
        string? clientId = configuration["Authentication:Yahoo:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return;
        }

        authentication.AddOpenIdConnect("Yahoo", options =>
        {
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.Authority = "https://api.login.yahoo.com";
            options.MetadataAddress =
                "https://api.login.yahoo.com/.well-known/openid-configuration";
            options.ClientId = clientId;
            options.ClientSecret = clientSecret;
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
            options.ClaimActions.MapUniqueJsonKey(
                ClaimTypes.NameIdentifier
                , "sub");
            options.ClaimActions.MapUniqueJsonKey(
                ClaimTypes.Name
                , "name");
            options.ClaimActions.MapUniqueJsonKey(
                ClaimTypes.Email
                , "email");
        });
    }

    private static void AddApple(
        AuthenticationBuilder authentication
        , IConfiguration configuration
        , string? clientSecret)
    {
        string? clientId = configuration["Authentication:Apple:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return;
        }

        authentication.AddOpenIdConnect("Apple", options =>
        {
            options.SignInScheme = IdentityConstants.ExternalScheme;
            options.Authority = "https://appleid.apple.com";
            options.MetadataAddress =
                "https://appleid.apple.com/.well-known/openid-configuration";
            options.ClientId = clientId;
            options.ClientSecret = clientSecret;
            options.CallbackPath = "/signin-apple";
            options.ResponseType = "code";
            options.ResponseMode = "form_post";
            options.RequireHttpsMetadata = true;
            options.SaveTokens = false;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("name");
            options.Scope.Add("email");
            options.ClaimActions.MapUniqueJsonKey(
                ClaimTypes.NameIdentifier
                , "sub");
            options.ClaimActions.MapUniqueJsonKey(
                ClaimTypes.Email
                , "email");
        });
    }

    private static void AddStaffOidc(
        AuthenticationBuilder authentication
        , IConfiguration configuration
        , string? clientSecret)
    {
        StaffOidcOptions staffOidc = configuration
            .GetSection(StaffOidcOptions.SectionName)
            .Get<StaffOidcOptions>()
            ?? new StaffOidcOptions();

        staffOidc.ClientSecret = clientSecret;
        if (!staffOidc.Enabled)
        {
            return;
        }

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
            options.Events = BuildStaffOidcEvents();
        });
    }

    private static OpenIdConnectEvents BuildStaffOidcEvents()
    {
        return new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.Prompt = "login";

                string correlationId = Guid.NewGuid().ToString("N");
                context.Properties.Items["aegis.auth.correlation"] =
                    correlationId;

                RequestPortalOperationsFlowPublisher flows =
                    context.HttpContext.RequestServices
                        .GetRequiredService<RequestPortalOperationsFlowPublisher>();

                flows.TryEnqueueAuthentication(
                    correlationId
                    , "IdentityProvider");

                return Task.CompletedTask;
            }
            , OnTokenValidated = async context =>
            {
                string correlationId = GetCorrelationId(context.Properties);

                RequestPortalOperationsFlowPublisher flows =
                    context.HttpContext.RequestServices
                        .GetRequiredService<RequestPortalOperationsFlowPublisher>();

                if (context.Principal is null)
                {
                    flows.TryEnqueueAuthentication(
                        correlationId
                        , "Callback"
                        , "Identity provider callback returned no principal."
                        , failed: true);

                    context.Fail(
                        "The staff identity provider returned no principal.");
                    return;
                }

                flows.TryEnqueueAuthentication(
                    correlationId
                    , "Claims");

                StaffOidcPrincipalService service =
                    context.HttpContext.RequestServices
                        .GetRequiredService<StaffOidcPrincipalService>();

                StaffOidcPrincipalResult result =
                    await service.BuildPrincipalAsync(
                        context.Principal
                        , context.HttpContext.RequestAborted);

                if (!result.Allowed || result.Principal is null)
                {
                    flows.TryEnqueueAuthentication(
                        correlationId
                        , "Authorization"
                        , result.FailureCode ?? "Staff authorization denied."
                        , failed: true);

                    context.Fail(
                        result.FailureCode ?? "FFP staff access denied.");
                    return;
                }

                context.Principal = result.Principal;

                flows.TryEnqueueAuthentication(
                    correlationId
                    , "Session");
            }
            , OnRemoteFailure = context =>
            {
                string correlationId = GetCorrelationId(context.Properties);

                RequestPortalOperationsFlowPublisher flows =
                    context.HttpContext.RequestServices
                        .GetRequiredService<RequestPortalOperationsFlowPublisher>();

                flows.TryEnqueueAuthentication(
                    correlationId
                    , "Callback"
                    , "Remote authentication failed before a session was established."
                    , failed: true);

                context.HandleResponse();
                context.Response.Redirect("/Account/StaffAccessDenied");
                return Task.CompletedTask;
            }
        };
    }

    private static string GetCorrelationId(
        AuthenticationProperties? properties)
    {
        if (properties?.Items.TryGetValue(
                "aegis.auth.correlation"
                , out string? value) == true &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Guid.NewGuid().ToString("N");
    }
}
