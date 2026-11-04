using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

[AllowAnonymous]
public sealed class StaffLoginModel : PageModel
{
    private readonly StaffOidcOptions options;

    public StaffLoginModel(IOptions<StaffOidcOptions> options)
    {
        this.options = options.Value;
    }

    public IActionResult OnGet(string? returnUrl = null)
    {
        if (!options.Enabled)
        {
            return RedirectToPage("/Account/StaffAccessDenied", new { reason = "not-configured" });
        }

        AuthenticationProperties properties = new()
        {
            RedirectUri = Url.Page("/Account/StaffComplete", new { returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null }) ?? "/Account/StaffComplete",
            IsPersistent = false
        };
        return Challenge(properties, StaffOidcOptions.Scheme);
    }
}
