using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

public sealed class LogoutModel : PageModel
{
    private readonly SignInManager<ApplicationUser> signInManager;

    public LogoutModel(SignInManager<ApplicationUser> signInManager)
    {
        this.signInManager = signInManager;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await signInManager.SignOutAsync();
        await HttpContext.SignOutAsync(StaffOidcOptions.StaffCookieScheme);
        return RedirectToPage("/Index");
    }
}
