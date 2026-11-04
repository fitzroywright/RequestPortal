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
            options.AddPolicy("StaffAdministration", policy => {
                policy.AuthenticationSchemes.Add(StaffOidcOptions.StaffCookieScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);
                policy.RequireClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access);
                policy.RequireClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity);
            });
            options.AddPolicy(
                "PortalRepresentative"
                , policy =>
                {
                    policy.AuthenticationSchemes.Add(
                        IdentityConstants.ApplicationScheme);
                    policy.RequireAuthenticatedUser();
                });

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
                    policy.AuthenticationSchemes.Add(PortalMessageAuthentication.Scheme);
                    policy.RequireAuthenticatedUser();
                    policy.RequireAssertion(context => !context.User.HasClaim(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue) ||
                        context.User.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
                });
        });

        builder.Services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder(
                "/Dashboard"
                , "PortalRepresentative");
            options.Conventions.AuthorizeFolder("/Administration", "StaffAdministration");
            options.Conventions.AuthorizeFolder(
                "/Staff"
                , PortalPolicies.FfpStaff);
            options.Conventions.AuthorizeFolder(
                "/Management"
                , PortalPolicies.ManagementDashboard);
            options.Conventions.AuthorizeFolder(
                "/Organizations"
                , "PortalRepresentative");
            options.Conventions.AuthorizeFolder(
                "/Requests"
                , "PortalRepresentative");
            options.Conventions.AuthorizeFolder(
                "/Messages"
                , "PortalMessages");
            options.Conventions.AuthorizePage("/Documents/Preview", "PortalMessages");
            options.Conventions.AuthorizePage(
                "/Messages/Assigned"
                , PortalPolicies.FfpStaff);
            options.Conventions.AuthorizePage(
                "/Messages/Unassigned"
                , PortalPolicies.FfpStaff);
            options.Conventions.AuthorizePage(
                "/ExternalReview/Index"
                , "ExternalReview");
        });
    }
}
