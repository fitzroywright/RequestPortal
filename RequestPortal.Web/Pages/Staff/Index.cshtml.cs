using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Pages.Staff;

public sealed class IndexModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly IStaffOperationsService staffOperationsService;
    private readonly RequestPortalDbContext dbContext;

    public IndexModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IStaffOperationsService staffOperationsService,
        RequestPortalDbContext dbContext)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.staffOperationsService = staffOperationsService;
        this.dbContext = dbContext;
    }

    public IReadOnlyList<OrganizationSummary> PendingOrganizations { get; private set; } = Array.Empty<OrganizationSummary>();

    public IReadOnlyList<RequestSummary> SubmittedRequests { get; private set; } = Array.Empty<RequestSummary>();

    public IReadOnlyDictionary<Guid, string> UnverifiedQuickIntakeApplicants { get; private set; } = new Dictionary<Guid, string>();

    public StaffOperationsSnapshot Operations { get; private set; } = new(
        Array.Empty<PendingOrganizationWorkItem>(),
        Array.Empty<OrganizationReverificationWorkItem>(),
        Array.Empty<RequestWorkItem>(),
        Array.Empty<RequestWorkItem>(),
        Array.Empty<DocumentWorkItem>(),
        Array.Empty<DeadLetterWorkItem>(),
        Array.Empty<NotificationDeliveryWorkItem>());

    public int TotalAttentionItems =>
        Operations.PendingOrganizations.Count +
        Operations.ReverificationOrganizations.Count +
        Operations.OpenReviewRequests.Count +
        Operations.ActionRequiredRequests.Count +
        Operations.DocumentsAwaitingVerification.Count +
        Operations.DeadLetters.Count +
        Operations.FailedNotificationDeliveries.Count;

    public int OldestReviewAgeDays =>
        Operations.OpenReviewRequests.Count == 0
            ? 0
            : Operations.OpenReviewRequests.Max(item => item.AgeDays);

    public async Task<IActionResult> OnGetAsync(string? requestCode = null)
    {
        if (!string.IsNullOrWhiteSpace(requestCode))
        {
            string reference = requestCode.Trim().ToUpperInvariant();
            Guid? requestId = await dbContext.AssistanceRequests.AsNoTracking()
                .Where(item => item.PublicReference == reference)
                .Select(item => (Guid?)item.Id).SingleOrDefaultAsync(HttpContext.RequestAborted);
            if (requestId.HasValue)
                return RedirectToPage("/Staff/Review", new { id = requestId.Value });
            TempData["Error"] = "No request was found with that request code.";
        }

        // These services share the scoped EF context; queries must be sequential.
        PendingOrganizations = await workflowService.GetPendingOrganizationsAsync(HttpContext.RequestAborted);
        SubmittedRequests = await workflowService.GetSubmittedRequestsAsync(HttpContext.RequestAborted);
        var submittedIds = SubmittedRequests.Select(item => item.Id).ToArray();
        UnverifiedQuickIntakeApplicants = await dbContext.QuickIntakes.AsNoTracking()
            .Where(item => submittedIds.Contains(item.RequestId) && item.ConvertedToUserId == null)
            .ToDictionaryAsync(item => item.RequestId, item => item.FirstName + " " + item.LastName, HttpContext.RequestAborted);
        Operations = await staffOperationsService.GetSnapshotAsync(HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostApproveOrganizationAsync(Guid organizationId)
    {
        ApplicationUser staffUser = await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated FFP staff user could not be resolved.");

        await workflowService.ApproveOrganizationAsync(
            staffUser.Id,
            organizationId,
            HttpContext.RequestAborted);

        TempData["Success"] = "Organization approved. Its registering representative is now the organization administrator.";
        return RedirectToPage();
    }
}
