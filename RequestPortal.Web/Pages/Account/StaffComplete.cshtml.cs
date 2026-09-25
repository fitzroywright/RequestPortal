using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Auditing;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

[Authorize(Policy = PortalPolicies.FfpStaff)]
public sealed class StaffCompleteModel : PageModel
{
    private readonly IBusinessAuditWriter auditWriter;

    public StaffCompleteModel(IBusinessAuditWriter auditWriter)
    {
        this.auditWriter = auditWriter;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        string? actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid actorId = Guid.TryParse(actor, out Guid parsed) ? parsed : Guid.Empty;
        await auditWriter.WriteAsync(
            actorId,
            "UserSignedIn",
            "User",
            actorId,
            null,
            "surface=FFPStaff; provider=OIDC",
            HttpContext.RequestAborted);
        bool canViewManagement = User.HasClaim(
            StaffOidcOptions.PermissionClaim,
            RequestPortalPermissions.ViewManagementDashboard);

        if (canViewManagement)
        {
            return RedirectToPage("/Management/Index");
        }

        return RedirectToPage("/Staff/Index");
    }
}
