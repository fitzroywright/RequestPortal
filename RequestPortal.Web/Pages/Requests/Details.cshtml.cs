using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Portal;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Requests;
using RequestPortal.Domain.Documents;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Pages.Requests;

public sealed class DetailsModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly IDocumentService documentService;
    private readonly IRequestLifecycleService lifecycleService;
    private readonly IProjectProposalService projectProposalService;
    private readonly IProjectPlanningService projectPlanningService;
    private readonly IRequestReferenceDataService referenceDataService;
    private readonly IFfpManagerIntegrationService ffpManagerIntegrationService;
    private readonly RequestPortalDbContext dbContext;

    public DetailsModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IDocumentService documentService,
        IRequestLifecycleService lifecycleService,
        IProjectProposalService projectProposalService,
        IProjectPlanningService projectPlanningService,
        IRequestReferenceDataService referenceDataService,
        IFfpManagerIntegrationService ffpManagerIntegrationService,
        RequestPortalDbContext dbContext)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.documentService = documentService;
        this.lifecycleService = lifecycleService;
        this.projectProposalService = projectProposalService;
        this.projectPlanningService = projectPlanningService;
        this.referenceDataService = referenceDataService;
        this.ffpManagerIntegrationService = ffpManagerIntegrationService;
        this.dbContext = dbContext;
    }

    public new RequestDetail? Request { get; private set; }
    public ProjectProposalDetail? ProjectProposal { get; private set; }
    public ProjectPlanningDetail? ProjectPlanning { get; private set; }
    public IReadOnlyList<DocumentSummary> Documents { get; private set; } = Array.Empty<DocumentSummary>();
    public IReadOnlyList<UnitOfMeasureOption> UnitsOfMeasure { get; private set; } = Array.Empty<UnitOfMeasureOption>();

    [BindProperty]
    public ItemInput Item { get; set; } = new();

    [BindProperty]
    public DocumentInput Document { get; set; } = new();

    [BindProperty]
    public CancelInput Cancellation { get; set; } = new();

    [BindProperty]
    public InformationResponseInput InformationResponse { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        return await LoadPageAsync(user, id);
    }

    public async Task<IActionResult> OnPostAddItemAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        UnitsOfMeasure = await referenceDataService.GetUnitsOfMeasureAsync(HttpContext.RequestAborted);

        bool validUnit = UnitsOfMeasure.Any(unit => unit.Id == Item.UnitOfMeasureId);
        if (!validUnit)
        {
            ModelState.AddModelError(nameof(Item.UnitOfMeasureId), "Select a valid unit of measure.");
        }

        if (!ModelState.IsValid)
        {
            return await LoadPageAsync(user, id);
        }

        await workflowService.AddRequestItemAsync(
            user.Id,
            id,
            Item.Description,
            Item.Quantity,
            Item.UnitOfMeasureId,
            HttpContext.RequestAborted);

        TempData["Success"] = "Requested item added.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUploadDocumentAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();

        if (Document.File is null || Document.File.Length <= 0)
        {
            ModelState.AddModelError("Document.File", "Select a file to upload.");
            return await LoadPageAsync(user, id);
        }

        await using System.IO.Stream content = Document.File.OpenReadStream();
        await documentService.UploadForRequestAsync(
            user.Id,
            id,
            Document.DocumentType,
            Document.File.FileName,
            Document.File.ContentType ?? "application/octet-stream",
            Document.File.Length,
            content,
            HttpContext.RequestAborted);

        TempData["Success"] = "Supporting document uploaded.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetDownloadAsync(Guid id, Guid documentId)
    {
        ApplicationUser user = await GetUserAsync();
        DocumentDownload? download = await documentService.DownloadAsync(
            user.Id,
            documentId,
            isStaff: false,
            HttpContext.RequestAborted);

        if (download is null)
        {
            return NotFound();
        }

        return File(download.Content, download.ContentType, download.FileName);
    }

    public async Task<IActionResult> OnPostSubmitAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        await workflowService.SubmitRequestAsync(user.Id, id, HttpContext.RequestAborted);
        TempData["Success"] = "Request submitted to FFP for review.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostResubmitAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        await workflowService.ResubmitRequestAsync(user.Id, id, HttpContext.RequestAborted);
        TempData["Success"] = "Request resubmitted to FFP.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostInformationResponseAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        RequestDetail? request = await workflowService.GetRequestAsync(
            user.Id,
            id,
            isStaff: false,
            HttpContext.RequestAborted);
        if (request is null)
        {
            return NotFound();
        }

        if (!string.Equals(request.Status, "MoreInformationRequired", StringComparison.Ordinal))
        {
            TempData["Error"] = "This request is not waiting for additional information.";
            return RedirectToPage(new { id });
        }

        if (string.IsNullOrWhiteSpace(InformationResponse.Message) && InformationResponse.DocumentIds.Count == 0)
        {
            ModelState.AddModelError("InformationResponse.Message", "Enter a response or select at least one supporting document.");
            return await LoadPageAsync(user, id);
        }

        string? applicationId = await dbContext.IntegrationLinks
            .AsNoTracking()
            .Where(link => link.RequestId == id && link.SystemName == "FFPManager")
            .Select(link => link.ExternalReference)
            .SingleOrDefaultAsync(HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            TempData["Error"] = "The FFP Manager application reference is not available yet.";
            return RedirectToPage(new { id });
        }

        await ffpManagerIntegrationService.RespondToInformationRequestAsync(
            id,
            applicationId,
            user.Id,
            InformationResponse.Message,
            InformationResponse.DocumentIds,
            HttpContext.RequestAborted);

        TempData["Success"] = "Your additional information was sent to FFP.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        if (string.IsNullOrWhiteSpace(Cancellation.Reason))
        {
            ModelState.AddModelError("Cancellation.Reason", "A cancellation reason is required.");
            return await LoadPageAsync(user, id);
        }

        await lifecycleService.CancelAsync(user.Id, id, Cancellation.Reason, HttpContext.RequestAborted);
        TempData["Success"] = "Request cancelled.";
        return RedirectToPage(new { id });
    }

    public string UnitName(Guid unitId)
    {
        UnitOfMeasureOption? unit = UnitsOfMeasure.FirstOrDefault(item => item.Id == unitId);
        return unit?.Name ?? "Unit";
    }

    private async Task<IActionResult> LoadPageAsync(ApplicationUser user, Guid id)
    {
        Request = await workflowService.GetRequestAsync(
            user.Id,
            id,
            isStaff: false,
            HttpContext.RequestAborted);
        if (Request is null)
        {
            return NotFound();
        }

        ProjectProposal = await projectProposalService.GetAsync(
            user.Id,
            id,
            isStaff: false,
            HttpContext.RequestAborted);
        if (ProjectProposal is not null)
        {
            ProjectPlanning = await projectPlanningService.GetAsync(
                user.Id,
                id,
                isStaff: false,
                HttpContext.RequestAborted);
        }

        Documents = await documentService.GetRequestDocumentsAsync(
            user.Id,
            id,
            isStaff: false,
            HttpContext.RequestAborted);
        UnitsOfMeasure = await referenceDataService.GetUnitsOfMeasureAsync(HttpContext.RequestAborted);

        if (Item.UnitOfMeasureId == Guid.Empty && UnitsOfMeasure.Count > 0)
        {
            Item.UnitOfMeasureId = UnitsOfMeasure[0].Id;
        }

        return Page();
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }

    public sealed class ItemInput
    {
        [Required]
        [StringLength(1000, MinimumLength = 2)]
        public string Description { get; set; } = string.Empty;

        [Range(typeof(decimal), "0.001", "999999999999")]
        public decimal Quantity { get; set; } = 1;

        [Required]
        public Guid UnitOfMeasureId { get; set; }
    }

    public sealed class DocumentInput
    {
        public DocumentType DocumentType { get; set; } = DocumentType.RequestSupport;
        public IFormFile? File { get; set; }
    }

    public sealed class CancelInput
    {
        [StringLength(1000)]
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class InformationResponseInput
    {
        [StringLength(4000)]
        public string Message { get; set; } = string.Empty;

        public List<Guid> DocumentIds { get; set; } = new();
    }
}
