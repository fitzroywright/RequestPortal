using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Startup;

internal static class StaffAuthenticationRegistration
{
    public static void Add(
        AuthenticationBuilder authentication
        , IConfiguration configuration
        , string? clientSecret)
    {
        StaffOidcOptions options = configuration
            .GetSection(StaffOidcOptions.SectionName)
            .Get<StaffOidcOptions>()
            ?? new StaffOidcOptions();

        options.ClientSecret = clientSecret;

        if (!options.Enabled)
        {
            return;
        }

        authentication.AddOpenIdConnect(
            StaffOidcOptions.Scheme
            , oidc =>
            {
                oidc.SignInScheme =
                    StaffOidcOptions.StaffCookieScheme;
                oidc.Authority = options.Authority;
                oidc.ClientId = options.ClientId;
                oidc.ClientSecret = options.ClientSecret;
                oidc.CallbackPath = options.CallbackPath;
                oidc.ResponseType = "code";
                oidc.UsePkce = true;
                oidc.RequireHttpsMetadata =
                    options.RequireHttpsMetadata;
                oidc.SaveTokens = false;
                oidc.GetClaimsFromUserInfoEndpoint = true;
                oidc.Scope.Clear();
                oidc.Scope.Add("openid");
                oidc.Scope.Add("profile");
                oidc.Scope.Add("email");
                oidc.Events = BuildEvents();
            });
    }

    private static OpenIdConnectEvents BuildEvents()
    {
        return new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.Prompt = "login";

                string correlationId =
                    Guid.NewGuid().ToString("N");

                context.Properties.Items[
                    "aegis.auth.correlation"] =
                    correlationId;

                RequestPortalOperationsFlowPublisher flows =
                    context.HttpContext.RequestServices
                        .GetRequiredService<
                            RequestPortalOperationsFlowPublisher>();

                flows.TryEnqueueAuthentication(
                    correlationId
                    , "IdentityProvider");

                return Task.CompletedTask;
            }
            , OnTokenValidated = async context =>
            {
                string correlationId =
                    GetCorrelationId(context.Properties);

                RequestPortalOperationsFlowPublisher flows =
                    context.HttpContext.RequestServices
                        .GetRequiredService<
                            RequestPortalOperationsFlowPublisher>();

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
                        .GetRequiredService<
                            StaffOidcPrincipalService>();

                StaffOidcPrincipalResult result =
                    await service.BuildPrincipalAsync(
                        context.Principal
                        , context.HttpContext.RequestAborted);

                if (!result.Allowed ||
                    result.Principal is null)
                {
                    flows.TryEnqueueAuthentication(
                        correlationId
                        , "Authorization"
                        , result.FailureCode
                            ?? "Staff authorization denied."
                        , failed: true);

                    context.Fail(
                        result.FailureCode
                            ?? "FFP staff access denied.");
                    return;
                }

                context.Principal = result.Principal;

                flows.TryEnqueueAuthentication(
                    correlationId
                    , "Session");
            }
            , OnRemoteFailure = context =>
            {
                string correlationId =
                    GetCorrelationId(context.Properties);

                RequestPortalOperationsFlowPublisher flows =
                    context.HttpContext.RequestServices
                        .GetRequiredService<
                            RequestPortalOperationsFlowPublisher>();

                flows.TryEnqueueAuthentication(
                    correlationId
                    , "Callback"
                    , "Remote authentication failed before a session was established."
                    , failed: true);

                context.HandleResponse();
                context.Response.Redirect(
                    "/Account/StaffAccessDenied");

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
