using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Management;
using RequestPortal.Application.Reviewers;

namespace RequestPortal.Web.Pages.ExternalReview;

public sealed class IndexModel : PageModel
{
    private readonly IManagementDashboardService dashboardService;
    private readonly IExternalReviewerService reviewerService;

    public IndexModel(
        IManagementDashboardService dashboardService,
        IExternalReviewerService reviewerService)
    {
        this.dashboardService = dashboardService;
        this.reviewerService = reviewerService;
    }

    public ManagementDashboardSnapshot Dashboard { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid invitationId) ||
            !await reviewerService.ValidateSessionAsync(invitationId, HttpContext.RequestAborted))
        {
            await HttpContext.SignOutAsync("ExternalReviewer");
            return RedirectToPage("/ExternalReview/AccessDenied");
        }

        Dashboard = await dashboardService.GetDashboardAsync(HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await HttpContext.SignOutAsync("ExternalReviewer");
        return RedirectToPage("/Index");
    }
}
