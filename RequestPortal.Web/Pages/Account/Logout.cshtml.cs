using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Application.Auditing;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

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
        Guid actor = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : Guid.Empty;
        await auditWriter.WriteAsync(
            actor,
            "UserSignedOut",
            "UserSession",
            actor,
            null,
            $"name={User.Identity?.Name}",
            HttpContext.RequestAborted);
        await signInManager.SignOutAsync();
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        await HttpContext.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
        await HttpContext.SignOutAsync(StaffOidcOptions.StaffCookieScheme);
        await HttpContext.SignOutAsync("ExternalReviewer");
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        Response.Headers.CacheControl = "no-store";
        return RedirectToPage("/Index");
    }
}
