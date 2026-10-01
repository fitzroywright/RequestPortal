using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Countries;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Management;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Organizations;
using RequestPortal.Application.Portal;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Requests;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Requests;

public sealed class CreateModel : PageModel
{
    private static readonly Regex htmlTags = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly IRequestDraftService requestDraftService;
    private readonly IProjectProposalService projectProposalService;
    private readonly ICountryPolicyService countryPolicyService;
    private readonly IRequestReferenceDataService referenceDataService;
    private readonly IRequestLocationService requestLocationService;
    private readonly IOrganizationProfileService organizationProfileService;
    private readonly IDocumentService documentService;
    private readonly IPortalMessageService messageService;
    private readonly ILogger<CreateModel> logger;

    public CreateModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IRequestDraftService requestDraftService,
        IProjectProposalService projectProposalService,
        ICountryPolicyService countryPolicyService,
        IRequestReferenceDataService referenceDataService,
        IRequestLocationService requestLocationService,
        IOrganizationProfileService organizationProfileService,
        IDocumentService documentService,
        IPortalMessageService messageService,
        ILogger<CreateModel> logger)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.requestDraftService = requestDraftService;
        this.projectProposalService = projectProposalService;
        this.countryPolicyService = countryPolicyService;
        this.referenceDataService = referenceDataService;
        this.requestLocationService = requestLocationService;
        this.organizationProfileService = organizationProfileService;
        this.documentService = documentService;
        this.messageService = messageService;
        this.logger = logger;
    }

    public IReadOnlyList<OrganizationRequesterOption> Organizations { get; private set; } = Array.Empty<OrganizationRequesterOption>();
    public IReadOnlyList<RequestCategoryOption> Categories { get; private set; } = Array.Empty<RequestCategoryOption>();
    public IReadOnlyList<ProgramClassificationOption> Programs { get; private set; } = Array.Empty<ProgramClassificationOption>();
    public IReadOnlyList<UnitOfMeasureOption> UnitsOfMeasure { get; private set; } = Array.Empty<UnitOfMeasureOption>();
    public string SubmittedByName { get; private set; } = string.Empty;
    public string? FailureDetails { get; private set; }
    public string? FailureStage { get; private set; }
    public string? FailureCorrelation { get; private set; }

    [BindProperty]
    public RequestInput Input { get; set; } = new();

    [BindProperty]
    public List<RequestItemInput> Items { get; set; } = [];

    [BindProperty]
    public ProjectInput Project { get; set; } = new();

    [BindProperty]
    public SupportingDocumentsInput SupportingDocuments { get; set; } = new();

    public async Task OnGetAsync(RequestKind? kind = null)
    {
        if (kind.HasValue)
        {
            Input.Kind = kind.Value;
        }

        ApplicationUser user = await GetUserAsync();
        await LoadPageDataAsync(user);
        if (Input.OrganizationId == Guid.Empty && Organizations.Count == 1)
        {
            Input.OrganizationId = Organizations[0].Id;
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        ApplicationUser user = await GetUserAsync();
        await LoadPageDataAsync(user);

        OrganizationRequesterOption? selectedOrganization = Organizations.FirstOrDefault(item => item.Id == Input.OrganizationId);
        if (selectedOrganization is null)
        {
            ModelState.AddModelError(nameof(Input.OrganizationId), "Select an approved organization that you are authorized to represent.");
        }
        else if (!selectedOrganization.ProfileComplete)
        {
            ModelState.AddModelError(nameof(Input.OrganizationId), $"Complete the organization profile before submitting this request. Missing: {selectedOrganization.MissingProfileFields}.");
        }

        if (!string.IsNullOrWhiteSpace(Input.Category) &&
            !Categories.Any(item => string.Equals(item.Name, Input.Category, StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(Input.Category), "Select a valid request category.");
        }

        if (!string.IsNullOrWhiteSpace(Input.ProgramCategory) &&
            !Programs.Any(item => string.Equals(item.Name, Input.ProgramCategory, StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(Input.ProgramCategory), "Select a valid program classification.");
        }

        if (Input.Kind == RequestKind.GoodsAndServices)
        {
            ValidateItems();
        }
        else
        {
            string plainText = htmlTags.Replace(Project.RichContentHtml ?? string.Empty, " ").Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase).Trim();
            if (plainText.Length < 20)
            {
                ModelState.AddModelError(nameof(Project.RichContentHtml), "Enter a meaningful project description before submitting.");
            }

            ValidateDocumentDeclarations();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await countryPolicyService.EnsureRequestSubmissionAllowedAsync(selectedOrganization!.CountryCode, HttpContext.RequestAborted);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(nameof(Input.OrganizationId), exception.Message);
            return Page();
        }

        Guid requestId = Guid.Empty;
        string stage = "creating the request";

        try
        {
            requestId = await workflowService.CreateRequestAsync(
                user.Id,
                Input.OrganizationId,
                Input.Summary,
                HttpContext.RequestAborted);

            stage = "saving request details";
            await requestDraftService.SetDetailsAsync(
            user.Id,
            requestId,
            null,
            Input.Kind,
            Input.Category,
            Input.Subcategory,
            Input.ProgramCategory,
            Input.Priority,
            Input.Location,
            Input.Circumstances,
            HttpContext.RequestAborted);

            stage = "saving request location";
            try
            {
                await requestLocationService.ResolveAndSaveAsync(
                requestId,
                Input.Location,
                Input.Latitude,
                Input.Longitude,
                HttpContext.RequestAborted);
        }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Request location enrichment could not be stored for request {RequestId}. Request creation will continue.",
                    requestId);
            }

            if (Input.Kind == RequestKind.Project)
            {
                stage = "initializing the project proposal";
                await projectProposalService.InitializeProjectAsync(user.Id, requestId, HttpContext.RequestAborted);
                stage = "saving the project proposal";
                await projectProposalService.SaveAsync(
                user.Id,
                requestId,
                string.IsNullOrWhiteSpace(Project.Title) ? "Project Proposal" : Project.Title,
                Project.RichContentHtml ?? string.Empty,
                HttpContext.RequestAborted);
                stage = "uploading project documents";
                await UploadProjectDocumentsAsync(user.Id, requestId);
            }
            else
            {
                stage = "saving requested items";
                foreach (RequestItemInput item in Items.Where(item => !item.IsBlank))
            {
                await workflowService.AddRequestItemAsync(
                    user.Id,
                    requestId,
                    item.ItemName,
                    item.Description,
                    item.Quantity,
                    item.UnitOfMeasureId,
                    item.Notes,
                    HttpContext.RequestAborted);
                }
            }

            stage = "submitting the request";
            await workflowService.SubmitRequestAsync(user.Id, requestId, HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string correlation =
                System.Diagnostics.Activity.Current?.Id
                ?? HttpContext.TraceIdentifier;

            logger.LogError(
                exception,
                "Create Request failed while {Stage}. RequestId={RequestId}; Correlation={Correlation}",
                stage,
                requestId,
                correlation);

            try
            {
                Guid? failedRequestId = requestId == Guid.Empty
                    ? null
                    : requestId;

                await messageService.SendStaffAlertAsync(
                    Input.OrganizationId,
                    failedRequestId,
                    "Request creation failed",
                    $"A representative encountered an error while {stage}. " +
                    $"Correlation: {correlation}. " +
                    $"Request: {(failedRequestId.HasValue ? failedRequestId.Value.ToString("D") : "not yet created")}. " +
                    $"Error: {exception.GetType().Name}: {exception.Message}",
                    HttpContext.RequestAborted);
            }
            catch (Exception alertException)
            {
                logger.LogWarning(
                    alertException,
                    "The Create Request failure could not also be posted to the FFP Staff inbox. Correlation={Correlation}",
                    correlation);
            }

            FailureStage = stage;
            FailureCorrelation = correlation;
            FailureDetails = exception.ToString();

            ModelState.AddModelError(
                string.Empty,
                $"The request could not be completed while {stage}. The full server exception is displayed below. Reference: {correlation}");

            return Page();
        }

        TempData["Success"] = "Request submitted to FFP for review.";
        return RedirectToPage("/Requests/Details", new { id = requestId });
    }

    private void ValidateItems()
    {
        List<RequestItemInput> populated = Items.Where(item => !item.IsBlank).ToList();
        if (populated.Count == 0)
        {
            ModelState.AddModelError(nameof(Items), "Add at least one requested item or service.");
            return;
        }

        for (int index = 0; index < populated.Count; index++)
        {
            RequestItemInput item = populated[index];
            if (string.IsNullOrWhiteSpace(item.ItemName))
                ModelState.AddModelError($"Items[{index}].ItemName", "Item or service is required.");
            if (string.IsNullOrWhiteSpace(item.Description))
                ModelState.AddModelError($"Items[{index}].Description", "Description or specification is required.");
            if (item.Quantity <= 0)
                ModelState.AddModelError($"Items[{index}].Quantity", "Quantity must be greater than zero.");
            if (!UnitsOfMeasure.Any(unit => unit.Id == item.UnitOfMeasureId))
                ModelState.AddModelError($"Items[{index}].UnitOfMeasureId", "Select a valid unit.");
        }
    }

    private void ValidateDocumentDeclarations()
    {
        ValidateDocumentCategory(SupportingDocuments.HasEvidence, SupportingDocuments.EvidenceFiles, "Evidence / Supporting Evidence");
        ValidateDocumentCategory(SupportingDocuments.HasEligibilityDocuments, SupportingDocuments.EligibilityFiles, "Eligibility Documents");
        ValidateDocumentCategory(SupportingDocuments.HasRegistrationMaterial, SupportingDocuments.RegistrationFiles, "Registration Certificate / Registration Material");
        ValidateDocumentCategory(SupportingDocuments.HasBudgetDocumentation, SupportingDocuments.BudgetFiles, "Budget / Budget Documentation");
        ValidateDocumentCategory(SupportingDocuments.HasProjectSchedule, SupportingDocuments.ScheduleFiles, "Milestones / Project Schedule");
        ValidateDocumentCategory(SupportingDocuments.HasAdditionalDocuments, SupportingDocuments.AdditionalFiles, "Follow-up / Additional Supporting Documents");
    }

    private void ValidateDocumentCategory(bool declared, IReadOnlyCollection<IFormFile> files, string label)
    {
        if (declared && files.Count == 0)
        {
            ModelState.AddModelError(nameof(SupportingDocuments), $"{label} is checked but no document has been attached.");
        }
    }

    private async Task UploadProjectDocumentsAsync(Guid userId, Guid requestId)
    {
        await UploadFilesAsync(userId, requestId, SupportingDocuments.EvidenceFiles, DocumentType.Evidence);
        await UploadFilesAsync(userId, requestId, SupportingDocuments.EligibilityFiles, DocumentType.EligibilityDocuments);
        await UploadFilesAsync(userId, requestId, SupportingDocuments.RegistrationFiles, DocumentType.RegistrationMaterial);
        await UploadFilesAsync(userId, requestId, SupportingDocuments.BudgetFiles, DocumentType.BudgetDocumentation);
        await UploadFilesAsync(userId, requestId, SupportingDocuments.ScheduleFiles, DocumentType.ProjectSchedule);
        await UploadFilesAsync(userId, requestId, SupportingDocuments.AdditionalFiles, DocumentType.AdditionalSupportingDocuments);
    }

    private async Task UploadFilesAsync(Guid userId, Guid requestId, IEnumerable<IFormFile> files, DocumentType documentType)
    {
        foreach (IFormFile file in files.Where(file => file.Length > 0))
        {
            await using System.IO.Stream content = file.OpenReadStream();
            await documentService.UploadForRequestAsync(
                userId,
                requestId,
                documentType,
                file.FileName,
                file.ContentType ?? "application/octet-stream",
                file.Length,
                content,
                HttpContext.RequestAborted);
        }
    }

    private async Task LoadPageDataAsync(ApplicationUser user)
    {
        SubmittedByName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName ?? user.Email ?? "Applicant" : user.DisplayName;
        Categories = await referenceDataService.GetCategoriesAsync(HttpContext.RequestAborted);
        Programs = await referenceDataService.GetProgramsAsync(HttpContext.RequestAborted);
        UnitsOfMeasure = await referenceDataService.GetUnitsOfMeasureAsync(HttpContext.RequestAborted);

        IReadOnlyList<OrganizationSummary> organizations = await workflowService.GetOrganizationsForUserAsync(user.Id, HttpContext.RequestAborted);
        IReadOnlyList<CountryOption> countries = await countryPolicyService.GetActiveCountriesAsync(HttpContext.RequestAborted);
        HashSet<string> requestEnabledCountries = countries
            .Where(country => country.RequestSubmissionEnabled)
            .Select(country => country.CountryCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<OrganizationRequesterOption> options = [];
        foreach (OrganizationSummary organization in organizations.Where(item =>
                     item.OrganizationStatus == "Approved" &&
                     item.MembershipStatus == "Approved" &&
                     requestEnabledCountries.Contains(item.CountryCode)))
        {
            OrganizationProfile? profile = await organizationProfileService.GetAsync(user.Id, organization.Id, HttpContext.RequestAborted);
            if (profile is null)
                continue;

            string[] missing = MissingProfileFields(profile);
            options.Add(new OrganizationRequesterOption(
                organization.Id,
                organization.Name,
                organization.CountryCode,
                organization.Role,
                missing.Length == 0,
                string.Join(", ", missing)));
        }

        Organizations = options;
    }

    private static string[] MissingProfileFields(OrganizationProfile profile)
    {
        List<string> missing = [];
        if (string.IsNullOrWhiteSpace(profile.RegistrationNumber)) missing.Add("registration number");
        if (string.IsNullOrWhiteSpace(profile.OrganizationType)) missing.Add("organization type");
        if (string.IsNullOrWhiteSpace(profile.Address)) missing.Add("address");
        if (string.IsNullOrWhiteSpace(profile.Phone)) missing.Add("phone");
        if (string.IsNullOrWhiteSpace(profile.Email)) missing.Add("email");
        if (string.IsNullOrWhiteSpace(profile.PrimaryContactName)) missing.Add("primary contact");
        if (profile.RegistrationExpiryDate.HasValue && profile.RegistrationExpiryDate.Value < DateOnly.FromDateTime(DateTime.UtcNow))
            missing.Add("current registration");
        return missing.ToArray();
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }

    public sealed record OrganizationRequesterOption(
        Guid Id,
        string Name,
        string CountryCode,
        string Role,
        bool ProfileComplete,
        string MissingProfileFields);

    public sealed class RequestInput
    {
        [Required]
        public Guid OrganizationId { get; set; }

        public RequestKind Kind { get; set; } = RequestKind.Project;

        [Required, StringLength(4000, MinimumLength = 10)]
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

        public double? Latitude { get; set; }

        public double? Longitude { get; set; }

        [StringLength(8000)]
        public string? Circumstances { get; set; }
    }

    public sealed class ProjectInput
    {
        [StringLength(500)]
        public string Title { get; set; } = "Project Proposal";

        public string RichContentHtml { get; set; } = string.Empty;
    }

    public sealed class RequestItemInput
    {
        [StringLength(250)]
        public string ItemName { get; set; } = string.Empty;

        [StringLength(2000)]
        public string Description { get; set; } = string.Empty;

        public decimal Quantity { get; set; } = 1;

        public Guid UnitOfMeasureId { get; set; }

        [StringLength(2000)]
        public string? Notes { get; set; }

        public bool IsBlank =>
            string.IsNullOrWhiteSpace(ItemName) &&
            string.IsNullOrWhiteSpace(Description) &&
            string.IsNullOrWhiteSpace(Notes);
    }

    public sealed class SupportingDocumentsInput
    {
        public bool HasEvidence { get; set; }
        public List<IFormFile> EvidenceFiles { get; set; } = [];
        public bool HasEligibilityDocuments { get; set; }
        public List<IFormFile> EligibilityFiles { get; set; } = [];
        public bool HasRegistrationMaterial { get; set; }
        public List<IFormFile> RegistrationFiles { get; set; } = [];
        public bool HasBudgetDocumentation { get; set; }
        public List<IFormFile> BudgetFiles { get; set; } = [];
        public bool HasProjectSchedule { get; set; }
        public List<IFormFile> ScheduleFiles { get; set; } = [];
        public bool HasAdditionalDocuments { get; set; }
        public List<IFormFile> AdditionalFiles { get; set; } = [];
    }
}
