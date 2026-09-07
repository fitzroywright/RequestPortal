using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Beneficiaries;
using RequestPortal.Application.Portal;
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

    public CreateModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IBeneficiaryService beneficiaryService,
        IRequestDraftService requestDraftService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.beneficiaryService = beneficiaryService;
        this.requestDraftService = requestDraftService;
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

        Guid? beneficiaryId = null;
        if (!string.IsNullOrWhiteSpace(Input.BeneficiaryFirstName) || !string.IsNullOrWhiteSpace(Input.BeneficiaryLastName))
        {
            if (string.IsNullOrWhiteSpace(Input.BeneficiaryFirstName) || string.IsNullOrWhiteSpace(Input.BeneficiaryLastName))
            {
                ModelState.AddModelError(string.Empty, "Both beneficiary first and last name are required when adding a beneficiary.");
                await LoadOrganizationsAsync(user.Id);
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

        Guid requestId = await workflowService.CreateRequestAsync(
            user.Id,
            Input.OrganizationId,
            Input.Summary,
            HttpContext.RequestAborted);

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

        TempData["Success"] = "Draft created. Add at least one requested item and any supporting documents before submission.";
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

        [StringLength(150)]
        public string? Category { get; set; }

        [StringLength(150)]
        public string? Subcategory { get; set; }

        [StringLength(150)]
        public string? ProgramCategory { get; set; }

        public RequestPriority Priority { get; set; } = RequestPriority.Normal;

        [StringLength(500)]
        public string? Location { get; set; }

        [StringLength(8000)]
        public string? Circumstances { get; set; }

        [StringLength(150)]
        public string BeneficiaryFirstName { get; set; } = string.Empty;

        [StringLength(150)]
        public string BeneficiaryLastName { get; set; } = string.Empty;

        public DateOnly? BeneficiaryDateOfBirth { get; set; }

        [StringLength(250)]
        public string? ParentOrGuardian { get; set; }
    }
}
