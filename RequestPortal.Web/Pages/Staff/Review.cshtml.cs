using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Delivery;
using RequestPortal.Application.Portal;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Requests;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Staff;

public sealed class ReviewModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly IRequestLifecycleService lifecycleService;
    private readonly IDocumentService documentService;
    private readonly IProjectProposalService projectProposalService;
    private readonly IProjectPlanningService projectPlanningService;
    private readonly IRequestReferenceDataService referenceDataService;
    private readonly IDeliveryService deliveryService;

    public ReviewModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IRequestLifecycleService lifecycleService,
        IDocumentService documentService,
        IProjectProposalService projectProposalService,
        IProjectPlanningService projectPlanningService,
        IRequestReferenceDataService referenceDataService,
        IDeliveryService deliveryService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.lifecycleService = lifecycleService;
        this.documentService = documentService;
        this.projectProposalService = projectProposalService;
        this.projectPlanningService = projectPlanningService;
        this.referenceDataService = referenceDataService;
        this.deliveryService = deliveryService;
    }

    public new RequestDetail? Request { get; private set; }
    public ProjectProposalDetail? ProjectProposal { get; private set; }
    public ProjectPlanningDetail? ProjectPlanning { get; private set; }
    public IReadOnlyList<DocumentSummary> Documents { get; private set; } = Array.Empty<DocumentSummary>();
    public IReadOnlyList<DeliveryView> Deliveries { get; private set; } = Array.Empty<DeliveryView>();
    public IReadOnlyList<UnitOfMeasureOption> UnitsOfMeasure { get; private set; } = Array.Empty<UnitOfMeasureOption>();

    [BindProperty, StringLength(4000)] public string DecisionReason { get; set; } = string.Empty;
    [BindProperty, StringLength(4000)] public string DuplicateReason { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        return await LoadPageAsync(staffUser, id);
    }

    public async Task<IActionResult> OnGetDownloadAsync(Guid id, Guid documentId)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        DocumentDownload? download = await documentService.DownloadAsync(
            staffUser.Id,
            documentId,
            isStaff: true,
            HttpContext.RequestAborted);

        if (download is null) return NotFound();
        return File(download.Content, download.ContentType, download.FileName);
    }

    public async Task<IActionResult> OnGetProposalDocxAsync(Guid id)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        ProjectProposalExport export = await projectProposalService.ExportDocxAsync(
            staffUser.Id, id, isStaff: true, HttpContext.RequestAborted);
        return File(export.Content, export.ContentType, export.FileName);
    }

    public async Task<IActionResult> OnGetProposalPdfAsync(Guid id)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        ProjectProposalExport export = await projectProposalService.ExportPdfAsync(
            staffUser.Id, id, isStaff: true, HttpContext.RequestAborted);
        return File(export.Content, export.ContentType, export.FileName);
    }

    public async Task<IActionResult> OnPostMoreInformationAsync(Guid id)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        if (string.IsNullOrWhiteSpace(DecisionReason))
        {
            ModelState.AddModelError(nameof(DecisionReason), "Explain what additional information is required.");
            return await LoadPageAsync(staffUser, id);
        }

        await workflowService.RequestMoreInformationAsync(staffUser.Id, id, DecisionReason.Trim(), HttpContext.RequestAborted);
        TempData["Success"] = "The organization will see that more information is required.";
        return RedirectToPage("/Staff/Index");
    }

    public async Task<IActionResult> OnPostDuplicateAsync(Guid id)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        if (string.IsNullOrWhiteSpace(DuplicateReason))
        {
            ModelState.AddModelError(nameof(DuplicateReason), "Explain which request this duplicates or why it is considered a duplicate.");
            return await LoadPageAsync(staffUser, id);
        }

        await lifecycleService.MarkDuplicateAsync(staffUser.Id, id, DuplicateReason.Trim(), HttpContext.RequestAborted);
        TempData["Success"] = "Request marked as duplicate.";
        return RedirectToPage("/Staff/Index");
    }

    public string UnitName(Guid unitId)
    {
        foreach (UnitOfMeasureOption unit in UnitsOfMeasure)
            if (unit.Id == unitId) return unit.Name;
        return "Unit";
    }

    private async Task<IActionResult> LoadPageAsync(ApplicationUser staffUser, Guid id)
    {
        Request = await workflowService.GetRequestAsync(staffUser.Id, id, isStaff: true, HttpContext.RequestAborted);
        if (Request is null) return NotFound();

        ProjectProposal = await projectProposalService.GetAsync(staffUser.Id, id, isStaff: true, HttpContext.RequestAborted);
        if (ProjectProposal is not null)
            ProjectPlanning = await projectPlanningService.GetAsync(staffUser.Id, id, isStaff: true, HttpContext.RequestAborted);

        Documents = await documentService.GetRequestDocumentsAsync(staffUser.Id, id, isStaff: true, HttpContext.RequestAborted);
        Deliveries = await deliveryService.GetForRequestAsync(id, HttpContext.RequestAborted);
        UnitsOfMeasure = await referenceDataService.GetUnitsOfMeasureAsync(HttpContext.RequestAborted);
        return Page();
    }

    private async Task<ApplicationUser> GetStaffUserAsync() =>
        await userManager.GetUserAsync(User)
        ?? throw new InvalidOperationException("Authenticated FFP staff user could not be resolved.");
}
