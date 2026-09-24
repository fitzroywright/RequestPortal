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

namespace RequestPortal.Web.Pages.Staff;

public sealed class IndexModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly IStaffOperationsService staffOperationsService;

    public IndexModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IStaffOperationsService staffOperationsService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.staffOperationsService = staffOperationsService;
    }

    public IReadOnlyList<OrganizationSummary> PendingOrganizations { get; private set; } = Array.Empty<OrganizationSummary>();

    public IReadOnlyList<RequestSummary> SubmittedRequests { get; private set; } = Array.Empty<RequestSummary>();

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

    public async Task OnGetAsync()
    {
        Task<IReadOnlyList<OrganizationSummary>> pendingOrganizationsTask =
            workflowService.GetPendingOrganizationsAsync(HttpContext.RequestAborted);
        Task<IReadOnlyList<RequestSummary>> submittedRequestsTask =
            workflowService.GetSubmittedRequestsAsync(HttpContext.RequestAborted);
        Task<StaffOperationsSnapshot> operationsTask =
            staffOperationsService.GetSnapshotAsync(HttpContext.RequestAborted);

        await Task.WhenAll(
            pendingOrganizationsTask,
            submittedRequestsTask,
            operationsTask);

        PendingOrganizations = await pendingOrganizationsTask;
        SubmittedRequests = await submittedRequestsTask;
        Operations = await operationsTask;
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
