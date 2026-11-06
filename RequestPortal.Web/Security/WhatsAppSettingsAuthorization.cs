using System.Security.Claims;
using Common.Security.Abstractions;
using Microsoft.AspNetCore.Authorization;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Web.Security;

public sealed class WhatsAppSettingsAdministratorRequirement : IAuthorizationRequirement
{
    public const string Policy = "WhatsAppSettingsAdministrator";
}

// Staff cookies intentionally project permissions, not role claims. Resolve the
// current existing role through Common.Security rather than trusting a role claim.
public sealed class WhatsAppSettingsAdministratorHandler(IAuthorizationStore roles)
    : AuthorizationHandler<WhatsAppSettingsAdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, WhatsAppSettingsAdministratorRequirement requirement)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true ||
            !user.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue) ||
            !user.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access) ||
            !user.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity) ||
            user.FindFirstValue(ClaimTypes.NameIdentifier) is not { } id) return;
        var direct = await roles.GetDirectRoleKeysAsync(id);
        var groups = await roles.GetRoleKeysForGroupsAsync(user.FindAll("directory_group").Select(x => x.Value).Distinct().ToArray());
        if (direct.Concat(groups).Contains(PortalRoles.Administrator, StringComparer.OrdinalIgnoreCase)) context.Succeed(requirement);
    }
}

// Cookie handlers normally redirect forbidden users to an access-denied page.
// Settings endpoints require an actual 403; leave every other policy unchanged.
public sealed class WhatsAppSettingsAuthorizationResultHandler : Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler
{
    private readonly Microsoft.AspNetCore.Authorization.Policy.AuthorizationMiddlewareResultHandler fallback = new();
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy,
        Microsoft.AspNetCore.Authorization.Policy.PolicyAuthorizationResult result)
    {
        if (policy.Requirements.OfType<WhatsAppSettingsAdministratorRequirement>().Any())
        {
            bool authenticated = context.User.Identity?.IsAuthenticated == true;
            if (result.Challenged && !authenticated)
            {
                var schemes = context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>();
                foreach (string scheme in new[] { StaffOidcOptions.StaffCookieScheme, Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme, "ExternalReviewer" })
                    if (await schemes.GetSchemeAsync(scheme) is not null &&
                        (await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions.AuthenticateAsync(context, scheme)).Succeeded)
                    { authenticated = true; break; }
            }
            if (result.Forbidden || result.Challenged && authenticated)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        await fallback.HandleAsync(next, context, policy, result);
    }
}
