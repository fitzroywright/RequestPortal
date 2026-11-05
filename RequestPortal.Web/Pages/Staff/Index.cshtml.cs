using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Portal;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Staff;

public sealed class IndexModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly IStaffOperationsService staffOperationsService;
    private readonly IStaffRequestSearchService searchService;

    public IndexModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IStaffOperationsService staffOperationsService,
        IStaffRequestSearchService searchService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.staffOperationsService = staffOperationsService;
        this.searchService = searchService;
    }

    public IReadOnlyList<OrganizationSummary> PendingOrganizations { get; private set; } = Array.Empty<OrganizationSummary>();

    public StaffRequestSearchResult Queue { get; private set; } = new([], 0, 1, 25, null);
    public IReadOnlyList<StaffRequestSearchRow> SubmittedRequests => Queue.Items;

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }
    [BindProperty(SupportsGet = true)]
    public string? Applicant { get; set; }
    [BindProperty(SupportsGet = true)]
    public string? Organization { get; set; }
    [BindProperty(SupportsGet = true)]
    public RequestStatus? WorkStatus { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)]
    public int PageSize { get; set; } = 25;
    public StaffRequestSearchResult? SearchResults { get; private set; }

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
        Queue.TotalCount +
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
        Search = (Search ?? requestCode)?.Trim();
        if (!string.IsNullOrWhiteSpace(Search))
        {
            SearchResults = await searchService.SearchAsync(Search, PageNumber, PageSize, HttpContext.RequestAborted);
            PageSize = SearchResults.PageSize;
            if (SearchResults.ExactReferenceId is Guid requestId)
                return RedirectToPage("/Staff/Review", new { id = requestId });
            return Page();
        }

        // These services share the scoped EF context; queries must be sequential.
        PendingOrganizations = await workflowService.GetPendingOrganizationsAsync(HttpContext.RequestAborted);
        Applicant = Applicant?.Trim();
        Organization = Organization?.Trim();
        Queue = await searchService.GetFilteredQueueAsync(Applicant, Organization, WorkStatus, PageNumber, PageSize, HttpContext.RequestAborted);
        PageSize = Queue.PageSize;
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
