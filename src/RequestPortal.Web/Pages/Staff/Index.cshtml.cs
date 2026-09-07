using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Staff;

public sealed class IndexModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;

    public IndexModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
    }

    public IReadOnlyList<OrganizationSummary> PendingOrganizations { get; private set; } = Array.Empty<OrganizationSummary>();

    public IReadOnlyList<RequestSummary> SubmittedRequests { get; private set; } = Array.Empty<RequestSummary>();

    public async Task OnGetAsync()
    {
        PendingOrganizations = await workflowService.GetPendingOrganizationsAsync(HttpContext.RequestAborted);
        SubmittedRequests = await workflowService.GetSubmittedRequestsAsync(HttpContext.RequestAborted);
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
