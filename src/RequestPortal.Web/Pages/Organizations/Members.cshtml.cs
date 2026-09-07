using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Organizations;

public sealed class MembersModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;

    public MembersModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
    }

    public IReadOnlyList<PendingMembershipSummary> Memberships { get; private set; } = Array.Empty<PendingMembershipSummary>();

    public Guid OrganizationId { get; private set; }

    public async Task OnGetAsync(Guid organizationId)
    {
        ApplicationUser user = await GetUserAsync();
        OrganizationId = organizationId;
        Memberships = await workflowService.GetPendingMembershipsAsync(
            user.Id,
            organizationId,
            HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostAsync(Guid organizationId, Guid membershipId)
    {
        ApplicationUser user = await GetUserAsync();
        await workflowService.ApproveMembershipAsync(
            user.Id,
            organizationId,
            membershipId,
            HttpContext.RequestAborted);

        TempData["Success"] = "Representative approved.";
        return RedirectToPage(new { organizationId });
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }
}
