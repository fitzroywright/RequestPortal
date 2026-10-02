using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Identity;
using RequestPortal.Web.Configuration;

namespace RequestPortal.Web.Startup;

internal static class RepresentativeAuthenticationRegistration
{
    public static void Add(
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
        string? clientId =
            configuration["Authentication:Google:ClientId"];

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
        string? clientId =
            configuration["Authentication:Microsoft:ClientId"];

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(clientSecret))
        {
            return;
        }

        authentication.AddMicrosoftAccount(options =>
        {
            options.ClientId = clientId;
            options.ClientSecret = clientSecret;
            options.Events.OnRedirectToAuthorizationEndpoint = context =>
            {
                string separator = context.RedirectUri.Contains('?') ? "&" : "?";
                string redirectUri = context.RedirectUri + separator + "prompt=select_account";
                context.Response.Redirect(redirectUri);
                return Task.CompletedTask;
            };
        });
    }

    private static void AddFacebook(
        AuthenticationBuilder authentication
        , IConfiguration configuration
        , string? appSecret)
    {
        string? appId =
            configuration["Authentication:Facebook:AppId"];

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
        string? clientId =
            configuration["Authentication:Yahoo:ClientId"];

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
        string? clientId =
            configuration["Authentication:Apple:ClientId"];

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
}
