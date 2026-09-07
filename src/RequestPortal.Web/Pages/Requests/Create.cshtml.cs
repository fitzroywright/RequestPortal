using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Requests;

public sealed class CreateModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;

    public CreateModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
    }

    public IReadOnlyList<OrganizationSummary> Organizations { get; private set; } = Array.Empty<OrganizationSummary>();

    [BindProperty]
    public RequestInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        ApplicationUser user = await GetUserAsync();
        await LoadOrganizationsAsync(user.Id);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ApplicationUser user = await GetUserAsync();

        if (!ModelState.IsValid)
        {
            await LoadOrganizationsAsync(user.Id);
            return Page();
        }

        Guid requestId = await workflowService.CreateRequestAsync(
            user.Id,
            Input.OrganizationId,
            Input.Summary,
            HttpContext.RequestAborted);

        TempData["Success"] = "Draft created. Add at least one requested item before submission.";
        return RedirectToPage("/Requests/Details", new { id = requestId });
    }

    private async Task LoadOrganizationsAsync(Guid userId)
    {
        IReadOnlyList<OrganizationSummary> organizations = await workflowService.GetOrganizationsForUserAsync(
            userId,
            HttpContext.RequestAborted);

        Organizations = organizations
            .Where(organization =>
                organization.OrganizationStatus == "Approved" &&
                organization.MembershipStatus == "Approved")
            .ToArray();
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }

    public sealed class RequestInput
    {
        [Required]
        public Guid OrganizationId { get; set; }

        [Required]
        [StringLength(4000, MinimumLength = 10)]
        public string Summary { get; set; } = string.Empty;
    }
}
