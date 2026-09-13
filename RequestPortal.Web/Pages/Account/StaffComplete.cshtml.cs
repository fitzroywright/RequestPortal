using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

[Authorize(Policy = PortalPolicies.FfpStaff)]
public sealed class StaffCompleteModel : PageModel
{
    public IActionResult OnGet()
    {
        bool canViewManagement = User.HasClaim(
            StaffOidcOptions.PermissionClaim,
            RequestPortalPermissions.ViewManagementDashboard);

        if (!canViewManagement)
        {
            return RedirectToPage("/Account/StaffAccessDenied", new { reason = "permission" });
        }

        return RedirectToPage("/Management/Index");
    }
}
