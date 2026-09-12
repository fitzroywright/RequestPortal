using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Beneficiaries;
using RequestPortal.Application.Countries;
using RequestPortal.Application.Management;
using RequestPortal.Application.Portal;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Requests;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Requests;

public sealed class CreateModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly IBeneficiaryService beneficiaryService;
    private readonly IRequestDraftService requestDraftService;
    private readonly IProjectProposalService projectProposalService;
    private readonly ICountryPolicyService countryPolicyService;
    private readonly IRequestReferenceDataService referenceDataService;
    private readonly IRequestLocationService requestLocationService;

    public CreateModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IBeneficiaryService beneficiaryService,
        IRequestDraftService requestDraftService,
        IProjectProposalService projectProposalService,
        ICountryPolicyService countryPolicyService,
        IRequestReferenceDataService referenceDataService,
        IRequestLocationService requestLocationService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.beneficiaryService = beneficiaryService;
        this.requestDraftService = requestDraftService;
        this.projectProposalService = projectProposalService;
        this.countryPolicyService = countryPolicyService;
        this.referenceDataService = referenceDataService;
        this.requestLocationService = requestLocationService;
    }

    public IReadOnlyList<OrganizationSummary> Organizations { get; private set; } = Array.Empty<OrganizationSummary>();
    public IReadOnlyList<RequestCategoryOption> Categories { get; private set; } = Array.Empty<RequestCategoryOption>();
    public IReadOnlyList<ProgramClassificationOption> Programs { get; private set; } = Array.Empty<ProgramClassificationOption>();

    [BindProperty]
    public RequestInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        ApplicationUser user = await GetUserAsync();
        await LoadPageDataAsync(user.Id);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ApplicationUser user = await GetUserAsync();
        await LoadPageDataAsync(user.Id);

        OrganizationSummary? selectedOrganization = Organizations.FirstOrDefault(organization => organization.Id == Input.OrganizationId);
        if (selectedOrganization is null)
            ModelState.AddModelError(nameof(Input.OrganizationId), "Select an approved organization that you are authorized to represent.");

        if (!string.IsNullOrWhiteSpace(Input.Category) && !Categories.Any(item => string.Equals(item.Name, Input.Category, StringComparison.OrdinalIgnoreCase)))
            ModelState.AddModelError(nameof(Input.Category), "Select a valid request category.");

        if (!string.IsNullOrWhiteSpace(Input.ProgramCategory) && !Programs.Any(item => string.Equals(item.Name, Input.ProgramCategory, StringComparison.OrdinalIgnoreCase)))
            ModelState.AddModelError(nameof(Input.ProgramCategory), "Select a valid program classification.");

        if (!ModelState.IsValid) return Page();

        try
        {
            await countryPolicyService.EnsureRequestSubmissionAllowedAsync(selectedOrganization!.CountryCode, HttpContext.RequestAborted);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(nameof(Input.OrganizationId), exception.Message);
            return Page();
        }

        Guid? beneficiaryId = null;
        if (!string.IsNullOrWhiteSpace(Input.BeneficiaryFirstName) || !string.IsNullOrWhiteSpace(Input.BeneficiaryLastName))
        {
            if (string.IsNullOrWhiteSpace(Input.BeneficiaryFirstName) || string.IsNullOrWhiteSpace(Input.BeneficiaryLastName))
            {
                ModelState.AddModelError(string.Empty, "Both beneficiary first and last name are required when adding a beneficiary.");
                return Page();
            }

            beneficiaryId = await beneficiaryService.CreateAsync(
                user.Id,
                Input.OrganizationId,
                Input.BeneficiaryFirstName,
                Input.BeneficiaryLastName,
                Input.BeneficiaryDateOfBirth,
                Input.ParentOrGuardian,
                Input.Location,
                HttpContext.RequestAborted);
        }

        Guid requestId = await workflowService.CreateRequestAsync(user.Id, Input.OrganizationId, Input.Summary, HttpContext.RequestAborted);

        await requestDraftService.SetDetailsAsync(
            user.Id,
            requestId,
            beneficiaryId,
            Input.Category,
            Input.Subcategory,
            Input.ProgramCategory,
            Input.Priority,
            Input.Location,
            Input.Circumstances,
            HttpContext.RequestAborted);

        await requestLocationService.ResolveAndSaveAsync(
            requestId,
            Input.Location,
            Input.Latitude,
            Input.Longitude,
            HttpContext.RequestAborted);

        if (Input.Kind == RequestKind.Project)
        {
            await projectProposalService.InitializeProjectAsync(user.Id, requestId, HttpContext.RequestAborted);
            TempData["Success"] = "Project draft created. Build the narrative proposal, budget and milestones before submitting it.";
            return RedirectToPage("/Requests/Proposal", new { id = requestId });
        }

        TempData["Success"] = "Draft created. Add at least one requested item and any supporting documents before submission.";
        return RedirectToPage("/Requests/Details", new { id = requestId });
    }

    private async Task LoadPageDataAsync(Guid userId)
    {
        await LoadOrganizationsAsync(userId);
        Categories = await referenceDataService.GetCategoriesAsync(HttpContext.RequestAborted);
        Programs = await referenceDataService.GetProgramsAsync(HttpContext.RequestAborted);
    }

    private async Task LoadOrganizationsAsync(Guid userId)
    {
        IReadOnlyList<OrganizationSummary> organizations = await workflowService.GetOrganizationsForUserAsync(userId, HttpContext.RequestAborted);
        IReadOnlyList<CountryOption> countries = await countryPolicyService.GetActiveCountriesAsync(HttpContext.RequestAborted);
        HashSet<string> requestEnabledCountries = countries
            .Where(country => country.RequestSubmissionEnabled)
            .Select(country => country.CountryCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Organizations = organizations
            .Where(organization => organization.OrganizationStatus == "Approved" && organization.MembershipStatus == "Approved" && requestEnabledCountries.Contains(organization.CountryCode))
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
        public RequestKind Kind { get; set; } = RequestKind.GoodsAndServices;
        [Required, StringLength(4000, MinimumLength = 10)]
        public string Summary { get; set; } = string.Empty;
        [StringLength(150)] public string? Category { get; set; }
        [StringLength(150)] public string? Subcategory { get; set; }
        [StringLength(150)] public string? ProgramCategory { get; set; }
        public RequestPriority Priority { get; set; } = RequestPriority.Normal;
        [StringLength(500)] public string? Location { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        [StringLength(8000)] public string? Circumstances { get; set; }
        [StringLength(150)] public string BeneficiaryFirstName { get; set; } = string.Empty;
        [StringLength(150)] public string BeneficiaryLastName { get; set; } = string.Empty;
        public DateOnly? BeneficiaryDateOfBirth { get; set; }
        [StringLength(250)] public string? ParentOrGuardian { get; set; }
    }
}
