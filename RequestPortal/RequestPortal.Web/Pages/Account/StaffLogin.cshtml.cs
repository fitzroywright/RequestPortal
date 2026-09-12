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

    public IActionResult OnGet()
    {
        if (!options.Enabled)
        {
            return RedirectToPage("/Account/StaffAccessDenied", new { reason = "not-configured" });
        }

        AuthenticationProperties properties = new()
        {
            RedirectUri = Url.Page("/Account/StaffComplete") ?? "/Account/StaffComplete",
            IsPersistent = false
        };
        return Challenge(properties, StaffOidcOptions.Scheme);
    }
}
