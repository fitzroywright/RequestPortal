using Microsoft.AspNetCore.Identity;
using RequestPortal.Application.Security;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Startup;

internal static class AuthorizationServiceRegistration
{
    public static void Add(WebApplicationBuilder builder)
    {
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(
                PortalPolicies.FfpStaff
                , policy =>
                {
                    policy.AuthenticationSchemes.Add(
                        StaffOidcOptions.StaffCookieScheme);
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim(
                        StaffOidcOptions.StaffSurfaceClaim
                        , StaffOidcOptions.StaffSurfaceValue);
                    policy.RequireClaim(
                        StaffOidcOptions.PermissionClaim
                        , RequestPortalPermissions.Access);
                });

            options.AddPolicy(
                PortalPolicies.ManagementDashboard
                , policy =>
                {
                    policy.AuthenticationSchemes.Add(
                        StaffOidcOptions.StaffCookieScheme);
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim(
                        StaffOidcOptions.StaffSurfaceClaim
                        , StaffOidcOptions.StaffSurfaceValue);
                    policy.RequireClaim(
                        StaffOidcOptions.PermissionClaim
                        , RequestPortalPermissions.ViewManagementDashboard);
                });

            options.AddPolicy(
                "ExternalReview"
                , policy =>
                {
                    policy.AuthenticationSchemes.Add("ExternalReviewer");
                    policy.RequireAuthenticatedUser();
                    policy.RequireClaim(
                        "requestportal.external-review"
                        , "true");
                    policy.RequireClaim(
                        "requestportal.external-review.scope");
                });

            options.AddPolicy(
                "PortalMessages"
                , policy =>
                {
                    policy.AuthenticationSchemes.Add(
                        IdentityConstants.ApplicationScheme);
                    policy.AuthenticationSchemes.Add(
                        StaffOidcOptions.StaffCookieScheme);
                    policy.RequireAuthenticatedUser();
                });
        });

        builder.Services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/Dashboard");
            options.Conventions.AuthorizeFolder(
                "/Staff"
                , PortalPolicies.FfpStaff);
            options.Conventions.AuthorizePage(
                "/Staff/Reviewers"
                , PortalPolicies.ManagementDashboard);
            options.Conventions.AuthorizeFolder(
                "/Management"
                , PortalPolicies.ManagementDashboard);
            options.Conventions.AuthorizeFolder("/Organizations");
            options.Conventions.AuthorizeFolder("/Requests");
            options.Conventions.AuthorizeFolder(
                "/Messages"
                , "PortalMessages");
            options.Conventions.AuthorizePage(
                "/ExternalReview/Index"
                , "ExternalReview");
        });
    }
}
