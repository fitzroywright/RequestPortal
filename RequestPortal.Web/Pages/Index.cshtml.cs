using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RequestPortal.Web.Security;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RequestPortal.Web.Pages;

public sealed class IndexModel : PageModel
{
    public string FirstName { get; private set; } = "there";

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true &&
            User.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue))
        {
            return RedirectToPage("/Staff/Index");
        }
        if (User.Identity?.IsAuthenticated != true)
        {
            return Page();
        }

        string? value =
            User.FindFirstValue(ClaimTypes.GivenName)
            ?? User.FindFirstValue(ClaimTypes.Name);

        if (!string.IsNullOrWhiteSpace(value))
        {
            FirstName = value
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }
        return Page();
    }
}
