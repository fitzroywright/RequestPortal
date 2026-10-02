using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Countries;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Organizations;

public sealed class IndexModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly ICountryPolicyService countryPolicyService;
    private readonly RequestPortal.Application.Organizations.IOrganizationInvitationService invitationService;

    public IndexModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        ICountryPolicyService countryPolicyService,
        RequestPortal.Application.Organizations.IOrganizationInvitationService invitationService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.countryPolicyService = countryPolicyService;
        this.invitationService = invitationService;
    }

    public IReadOnlyList<OrganizationSummary> Organizations { get; private set; } = Array.Empty<OrganizationSummary>();

    public IReadOnlyList<RequestPortal.Application.Organizations.OrganizationInvitationView> PendingInvitations { get; private set; } = Array.Empty<RequestPortal.Application.Organizations.OrganizationInvitationView>();

    public IReadOnlyList<CountryOption> RegistrationCountries { get; private set; } = Array.Empty<CountryOption>();

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

        try
        {
            await countryPolicyService.EnsureOrganizationRegistrationAllowedAsync(
                Registration.CountryCode,
                HttpContext.RequestAborted);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(nameof(Registration.CountryCode), exception.Message);
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

    private async Task LoadAsync(Guid userId)
    {
        Organizations = await workflowService.GetOrganizationsForUserAsync(userId, HttpContext.RequestAborted);

        ApplicationUser user = await userManager.FindByIdAsync(userId.ToString())
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");

        PendingInvitations = await invitationService.GetPendingForUserAsync(
            userId,
            user.Email,
            HttpContext.RequestAborted);

        IReadOnlyList<CountryOption> countries = await countryPolicyService.GetActiveCountriesAsync(HttpContext.RequestAborted);
        RegistrationCountries = countries
            .Where(country => country.OrganizationRegistrationEnabled)
            .ToArray();
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
