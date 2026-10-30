using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RequestPortal.Web.Pages.Account;

[AllowAnonymous]
public sealed class StaffAccessDeniedModel : PageModel
{
    public string Message { get; private set; } = string.Empty;

    public void OnGet(string? reason)
    {
        Message = reason?.Trim().ToLowerInvariant() switch
        {
            "pending" => "Your staff identity is authenticated and Pending Access. An administrator must approve your account and assign staff permissions.",
            "not-configured" => "FFP Staff Authentication has not been commissioned for this Request Portal environment.",
            "permission" => "Your FFP staff identity was authenticated, but you do not have the ManagementDashboard.View permission required for the Management Dashboard.",
            _ => "FFP Staff Authentication could not be completed. Return to FFP Staff Sign-In and try again, or contact IT if the problem continues."
        };
    }
}
