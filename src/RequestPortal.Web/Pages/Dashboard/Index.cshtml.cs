using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Dashboard;

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

    public string DisplayName { get; private set; } = string.Empty;

    public IReadOnlyList<OrganizationSummary> Organizations { get; private set; } = Array.Empty<OrganizationSummary>();

    public IReadOnlyList<RequestSummary> Requests { get; private set; } = Array.Empty<RequestSummary>();

    public int ApprovedOrganizations => Organizations.Count(
        organization =>
            organization.OrganizationStatus == "Approved" &&
            organization.MembershipStatus == "Approved");

    public async Task OnGetAsync()
    {
        ApplicationUser user = await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");

        DisplayName = user.DisplayName;
        Organizations = await workflowService.GetOrganizationsForUserAsync(user.Id, HttpContext.RequestAborted);
        Requests = await workflowService.GetRequestsForUserAsync(user.Id, HttpContext.RequestAborted);
    }
}
