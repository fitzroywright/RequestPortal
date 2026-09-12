using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

[Authorize]
public sealed class StaffCompleteModel : PageModel
{
    public IActionResult OnGet()
    {
        bool isStaffSurface = User.HasClaim(
            StaffOidcOptions.StaffSurfaceClaim,
            StaffOidcOptions.StaffSurfaceValue);
        bool canViewManagement = User.HasClaim(
            StaffOidcOptions.PermissionClaim,
            RequestPortalPermissions.ViewManagementDashboard);

        if (!isStaffSurface || !canViewManagement)
        {
            return RedirectToPage("/Account/StaffAccessDenied", new { reason = "permission" });
        }

        return RedirectToPage("/Management/Index");
    }
}
