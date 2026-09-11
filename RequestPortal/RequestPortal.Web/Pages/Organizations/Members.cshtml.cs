using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Organizations;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Organizations;

public sealed class MembersModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IOrganizationMembershipGovernanceService governanceService;

    public MembersModel(
        UserManager<ApplicationUser> userManager,
        IOrganizationMembershipGovernanceService governanceService)
    {
        this.userManager = userManager;
        this.governanceService = governanceService;
    }

    public IReadOnlyList<OrganizationMembershipView> Memberships { get; private set; } = Array.Empty<OrganizationMembershipView>();

    public Guid OrganizationId { get; private set; }

    public async Task OnGetAsync(Guid organizationId)
    {
        ApplicationUser user = await GetUserAsync();
        OrganizationId = organizationId;
        Memberships = await governanceService.GetMembershipsAsync(
            user.Id,
            organizationId,
            HttpContext.RequestAborted);
    }

    public Task<IActionResult> OnPostApproveAsync(Guid organizationId, Guid membershipId)
    {
        return ExecuteAsync(
            organizationId,
            () => governanceService.ApproveAsync(
                GetUserId(),
                organizationId,
                membershipId,
                HttpContext.RequestAborted),
            "Representative approved.");
    }

    public Task<IActionResult> OnPostRejectAsync(Guid organizationId, Guid membershipId, string reason)
    {
        return ExecuteAsync(
            organizationId,
            () => governanceService.RejectAsync(
                GetUserId(),
                organizationId,
                membershipId,
                reason,
                HttpContext.RequestAborted),
            "Representative rejected.");
    }

    public Task<IActionResult> OnPostPromoteAsync(Guid organizationId, Guid membershipId)
    {
        return ExecuteAsync(
            organizationId,
            () => governanceService.PromoteAsync(
                GetUserId(),
                organizationId,
                membershipId,
                HttpContext.RequestAborted),
            "Representative promoted to organization administrator.");
    }

    public Task<IActionResult> OnPostDemoteAsync(Guid organizationId, Guid membershipId)
    {
        return ExecuteAsync(
            organizationId,
            () => governanceService.DemoteAsync(
                GetUserId(),
                organizationId,
                membershipId,
                HttpContext.RequestAborted),
            "Administrator changed to representative.");
    }

    public Task<IActionResult> OnPostRevokeAsync(Guid organizationId, Guid membershipId, string reason)
    {
        return ExecuteAsync(
            organizationId,
            () => governanceService.RevokeAsync(
                GetUserId(),
                organizationId,
                membershipId,
                reason,
                HttpContext.RequestAborted),
            "Representative access revoked.");
    }

    private async Task<IActionResult> ExecuteAsync(
        Guid organizationId,
        Func<Task> operation,
        string successMessage)
    {
        try
        {
            await operation();
            TempData["Success"] = successMessage;
        }
        catch (InvalidOperationException exception)
        {
            TempData["Error"] = exception.Message;
        }
        catch (ArgumentException exception)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage(new { organizationId });
    }

    private Guid GetUserId()
    {
        string? userId = userManager.GetUserId(User);
        if (!Guid.TryParse(userId, out Guid value))
        {
            throw new InvalidOperationException("Authenticated portal user could not be resolved.");
        }

        return value;
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }
}
