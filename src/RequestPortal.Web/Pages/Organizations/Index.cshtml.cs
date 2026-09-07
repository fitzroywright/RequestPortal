using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Organizations;

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

    public IReadOnlyList<OrganizationSummary> Organizations { get; private set; } = Array.Empty<OrganizationSummary>();

    public IReadOnlyList<OrganizationSummary> AvailableOrganizations { get; private set; } = Array.Empty<OrganizationSummary>();

    [BindProperty]
    public RegistrationInput Registration { get; set; } = new();

    public async Task OnGetAsync()
    {
        ApplicationUser user = await GetUserAsync();
        await LoadAsync(user.Id);
    }

    public async Task<IActionResult> OnPostRegisterAsync()
    {
        ApplicationUser user = await GetUserAsync();

        if (!ModelState.IsValid)
        {
            await LoadAsync(user.Id);
            return Page();
        }

        await workflowService.RegisterOrganizationAsync(
            user.Id,
            Registration.Name,
            Registration.CountryCode,
            HttpContext.RequestAborted);

        TempData["Success"] = "Organization registration submitted to FFP for approval.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRequestMembershipAsync(Guid organizationId)
    {
        ApplicationUser user = await GetUserAsync();
        await workflowService.RequestMembershipAsync(user.Id, organizationId, HttpContext.RequestAborted);
        TempData["Success"] = "Membership request submitted to the organization's administrator.";
        return RedirectToPage();
    }

    private async Task LoadAsync(Guid userId)
    {
        Organizations = await workflowService.GetOrganizationsForUserAsync(userId, HttpContext.RequestAborted);
        AvailableOrganizations = await workflowService.GetAvailableOrganizationsAsync(userId, HttpContext.RequestAborted);
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }

    public sealed class RegistrationInput
    {
        [Required]
        [StringLength(250, MinimumLength = 3)]
        [Display(Name = "Organization name")]
        public string Name { get; set; } = string.Empty;

        [Required]
        [StringLength(2, MinimumLength = 2)]
        public string CountryCode { get; set; } = "JM";
    }
}
