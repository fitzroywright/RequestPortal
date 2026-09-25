using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Auditing;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

[IgnoreAntiforgeryToken]
public sealed class LogoutModel : PageModel
{
    private readonly SignInManager<ApplicationUser> signInManager;
    private readonly IBusinessAuditWriter auditWriter;

    public LogoutModel(
        SignInManager<ApplicationUser> signInManager,
        IBusinessAuditWriter auditWriter)
    {
        this.signInManager = signInManager;
        this.auditWriter = auditWriter;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        string? actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid actorId = Guid.TryParse(actor, out Guid parsed) ? parsed : Guid.Empty;
        if (actorId != Guid.Empty)
        {
            await auditWriter.WriteAsync(
                actorId,
                "UserSignedOut",
                "User",
                actorId,
                null,
                null,
                HttpContext.RequestAborted);
        }

        await signInManager.SignOutAsync();
        await HttpContext.SignOutAsync(StaffOidcOptions.StaffCookieScheme);
        return RedirectToPage("/Index");
    }
}
