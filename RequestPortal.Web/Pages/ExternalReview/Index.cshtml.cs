using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Management;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Reviewers;

namespace RequestPortal.Web.Pages.ExternalReview;

public sealed class IndexModel : PageModel
{
    private readonly IManagementDashboardService dashboardService;
    private readonly IExternalReviewerService reviewerService;
    private readonly IBusinessAuditWriter auditWriter;

    public IndexModel(
        IManagementDashboardService dashboardService,
        IExternalReviewerService reviewerService,
        IBusinessAuditWriter auditWriter)
    {
        this.dashboardService = dashboardService;
        this.reviewerService = reviewerService;
        this.auditWriter = auditWriter;
    }

    public ManagementDashboardSnapshot Dashboard { get; private set; } = null!;
    public ExternalReviewerInvitationView ReviewerScope { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid invitationId) ||
            !await reviewerService.ValidateSessionAsync(invitationId, HttpContext.RequestAborted))
        {
            await HttpContext.SignOutAsync("ExternalReviewer");
            return RedirectToPage("/ExternalReview/AccessDenied");
        }

        ExternalReviewerInvitationView? scope =
            await reviewerService.GetActiveSessionAsync(invitationId, HttpContext.RequestAborted);
        if (scope is null)
        {
            await HttpContext.SignOutAsync("ExternalReviewer");
            return RedirectToPage("/ExternalReview/AccessDenied");
        }

        ReviewerScope = scope;
        Dashboard = await dashboardService.GetDashboardAsync(
            new ManagementDashboardFilter(
                scope.OrganizationId,
                scope.RequestId,
                scope.FromUtc,
                scope.ToUtc),
            HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        if (Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid invitationId))
        {
            ExternalReviewerInvitationView? scope =
                await reviewerService.GetActiveSessionAsync(invitationId, HttpContext.RequestAborted);
            await auditWriter.WriteAsync(
                Guid.Empty,
                "ExternalReviewerSignedOut",
                "ExternalReviewerInvitation",
                invitationId,
                scope?.OrganizationId,
                scope is null ? null : $"email={scope.Email}; scope={scope.Scope}",
                HttpContext.RequestAborted);
        }
        await HttpContext.SignOutAsync("ExternalReviewer");
        return RedirectToPage("/Index");
    }
}
