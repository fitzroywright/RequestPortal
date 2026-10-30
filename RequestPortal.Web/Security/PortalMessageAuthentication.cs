using Microsoft.AspNetCore.Identity;

namespace RequestPortal.Web.Security;

/// <summary>Message authorization uses one current population, never a merged applicant/staff principal.</summary>
public static class PortalMessageAuthentication
{
    public const string Scheme="PortalMessagesIdentity";
    public static string SelectScheme(HttpContext context) => context.User.HasClaim(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue)
        ? StaffOidcOptions.StaffCookieScheme : IdentityConstants.ApplicationScheme;
}
