using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RequestPortal.Web.Pages.Account;

[AllowAnonymous]
public sealed class StaffAccessDeniedModel : PageModel
{
    public string Message { get; private set; } = string.Empty;

    public void OnGet(string? reason)
    {
        Message = string.Equals(reason, "not-configured", StringComparison.OrdinalIgnoreCase)
            ? "FFP Staff Authentication has not been commissioned for this Request Portal environment."
            : "Your FFP staff identity was authenticated, but you do not have the ManagementDashboard.View permission required for the Management Dashboard.";
    }
}
