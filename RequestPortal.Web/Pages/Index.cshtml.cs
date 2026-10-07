using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RequestPortal.Web.Pages;

public sealed class IndexModel : PageModel
{
    public string FirstName { get; private set; } = "there";

    public void OnGet()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return;
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
    }
}
