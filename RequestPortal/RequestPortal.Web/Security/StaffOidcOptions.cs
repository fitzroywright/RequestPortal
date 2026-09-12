namespace RequestPortal.Web.Security;

public sealed class StaffOidcOptions
{
    public const string SectionName = "Authentication:StaffOidc";
    public const string Scheme = "FfpStaffOidc";
    public const string StaffCookieScheme = "FfpStaffCookie";
    public const string StaffSurfaceClaim = "requestportal_auth_surface";
    public const string StaffSurfaceValue = "ffp_staff";
    public const string PermissionClaim = "requestportal_permission";

    public bool Enabled { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string CallbackPath { get; set; } = "/signin-ffp-staff";
    public bool RequireHttpsMetadata { get; set; } = true;
    public string UserNameClaim { get; set; } = "preferred_username";
    public string DisplayNameClaim { get; set; } = "name";
    public string EmailClaim { get; set; } = "email";
    public string[] GroupClaimTypes { get; set; } = ["groups", "group", "memberOf"];
}
