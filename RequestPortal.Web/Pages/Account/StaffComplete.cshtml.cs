using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Common.Security;
using RequestPortal.Application.Auditing;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

[Authorize(AuthenticationSchemes = StaffOidcOptions.StaffCookieScheme)]
public sealed class StaffCompleteModel : PageModel
{
    private readonly IBusinessAuditWriter auditWriter;

    public StaffCompleteModel(IBusinessAuditWriter auditWriter)
    {
        this.auditWriter = auditWriter;
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        if(!User.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access))
            return RedirectToPage("/Account/StaffAccessDenied",new {reason="pending"});
        Guid actor = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : Guid.Empty;
        await auditWriter.WriteAsync(
            actor,
            "StaffSignedIn",
            "UserSession",
            actor,
            null,
            $"name={User.Identity?.Name}",
            HttpContext.RequestAborted);
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToPage("/Staff/Index");
    }
}
