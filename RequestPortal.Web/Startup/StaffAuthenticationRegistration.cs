using Common.Security.Hosting;
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

        var identity = new global::Common.Security.Integration.ExternalIdentityRegistration(options.ProviderId,
            string.IsNullOrWhiteSpace(options.Issuer) ? options.Authority.TrimEnd('/') : options.Issuer,
            options.AllowedTenantIds.Length > 0 ? global::Common.Security.Models.AuthenticationProviderKind.EntraId : global::Common.Security.Models.AuthenticationProviderKind.External,
            options.AllowedTenantIds);
        authentication.AddCommonOpenIdConnect(new CommonOpenIdConnectOptions(StaffOidcOptions.Scheme,
            options.Authority, options.ClientId, clientSecret ?? throw new InvalidOperationException("Staff OIDC client secret is required."),
            options.CallbackPath, StaffOidcOptions.StaffCookieScheme, identity), async (context, proof) =>
            {
                var flows=context.HttpContext.RequestServices.GetRequiredService<RequestPortalOperationsFlowPublisher>();
                string correlation=GetCorrelationId(context.Properties);
                flows.TryEnqueueAuthentication(correlation,"Claims");
                var result=await context.HttpContext.RequestServices.GetRequiredService<StaffOidcPrincipalService>()
                    .BuildPrincipalAsync(proof,context.HttpContext.RequestAborted);
                if(!result.Allowed || result.Principal is null) { context.Fail(result.FailureCode ?? "Staff access denied."); return; }
                context.Principal=result.Principal;
                flows.TryEnqueueAuthentication(correlation,"Session");
            });
        authentication.Services.PostConfigure<OpenIdConnectOptions>(StaffOidcOptions.Scheme, oidc =>
        {
            var redirect=oidc.Events.OnRedirectToIdentityProvider;
            oidc.Events.OnRedirectToIdentityProvider=async context =>
            {
                await redirect(context);
                context.ProtocolMessage.Prompt="login";
                var correlation=Guid.NewGuid().ToString("N");
                context.Properties.Items["aegis.auth.correlation"]=correlation;
                context.HttpContext.RequestServices.GetRequiredService<RequestPortalOperationsFlowPublisher>()
                    .TryEnqueueAuthentication(correlation,"IdentityProvider");
            };
            oidc.Events.OnRemoteFailure=context =>
            {
                context.HttpContext.RequestServices.GetRequiredService<RequestPortalOperationsFlowPublisher>()
                    .TryEnqueueAuthentication(GetCorrelationId(context.Properties),"Callback","Remote authentication failed.",failed:true);
                context.HandleResponse(); context.Response.Redirect("/Account/StaffAccessDenied");
                return Task.CompletedTask;
            };
        });
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
