using RequestPortal.Application.Administration;
using RequestPortal.Domain.Documents;
using System;
using System.Collections.Generic;
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
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Staff;

public sealed class ReviewModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly IStaffDocumentContextService documentContextService;
    private readonly IDocumentService documentService;
    private readonly IProjectProposalService projectProposalService;
    private readonly IProjectPlanningService projectPlanningService;
    private readonly IRequestReferenceDataService referenceDataService;
    private readonly IDeliveryService deliveryService;
    private readonly IQuickIntakeService quickIntakeService;

    public ReviewModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IDocumentService documentService,
        IProjectProposalService projectProposalService,
        IProjectPlanningService projectPlanningService,
        IRequestReferenceDataService referenceDataService,
        IDeliveryService deliveryService,
        IQuickIntakeService quickIntakeService,
        IStaffDocumentContextService documentContextService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.documentService = documentService;
        this.documentContextService = documentContextService;
        this.projectProposalService = projectProposalService;
        this.projectPlanningService = projectPlanningService;
        this.referenceDataService = referenceDataService;
        this.deliveryService = deliveryService;
        this.quickIntakeService = quickIntakeService;
    }

    public new RequestDetail? Request { get; private set; }
    public ProjectProposalDetail? ProjectProposal { get; private set; }
    public ProjectPlanningDetail? ProjectPlanning { get; private set; }
    public IReadOnlyList<DocumentSummary> Documents { get; private set; } = Array.Empty<DocumentSummary>();
    public IReadOnlyList<DeliveryView> Deliveries { get; private set; } = Array.Empty<DeliveryView>();
    public IReadOnlyList<UnitOfMeasureOption> UnitsOfMeasure { get; private set; } = Array.Empty<UnitOfMeasureOption>();
    public QuickIntakeView? QuickIntake { get; private set; }

    public bool CanReviewQuickIntake =>
        User.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue) &&
        User.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access) &&
        User.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ReviewRequests);

    [BindProperty]
    [RequestPortal.Common.Contacts.ContactEmail]
    public string? ConversionEmail { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? SelectedDocumentId { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        return await LoadPageAsync(staffUser, id);
    }

    public async Task<IActionResult> OnPostConvertQuickIntakeAsync(Guid id)
    {
        if (!CanReviewQuickIntake) return Forbid(StaffOidcOptions.StaffCookieScheme);
        ApplicationUser staffUser = await GetStaffUserAsync();

        if (string.IsNullOrWhiteSpace(ConversionEmail))
        {
            ModelState.AddModelError(nameof(ConversionEmail), "Enter the email address of the Individual portal account.");
            return await LoadPageAsync(staffUser, id);
        }

        RequestPortal.Common.Contacts.InputContactNormalization.Apply(this,ModelState);
        if(!ModelState.IsValid)return await LoadPageAsync(staffUser,id);
        ApplicationUser? individual = await userManager.FindByEmailAsync(ConversionEmail.Trim());
        if (individual is null || individual.IsInternalStaff)
        {
            ModelState.AddModelError(nameof(ConversionEmail), "No Individual portal account was found for that email address.");
            return await LoadPageAsync(staffUser, id);
        }

        try
        {
            await quickIntakeService.ConvertToIndividualAsync(
                id,
                individual.Id,
                staffUser.Id,
                HttpContext.RequestAborted);

            TempData["Success"] = $"Quick Intake converted to the Individual account for {individual.DisplayName}.";
            return RedirectToPage(new { id });
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(nameof(ConversionEmail), exception.Message);
            return await LoadPageAsync(staffUser, id);
        }
    }

    public async Task<IActionResult> OnGetDownloadAsync(Guid id, Guid documentId)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        if (await CheckDocumentAsync(id, documentId) is IActionResult error) return error;
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

        if (SelectedDocumentId is Guid selectedId && await CheckDocumentAsync(id, selectedId) is IActionResult error)
            return error;

        Documents = await documentService.GetRequestDocumentsAsync(staffUser.Id, id, isStaff: true, HttpContext.RequestAborted);
        Deliveries = await deliveryService.GetForRequestAsync(id, HttpContext.RequestAborted);
        UnitsOfMeasure = await referenceDataService.GetUnitsOfMeasureAsync(HttpContext.RequestAborted);
        QuickIntake = CanReviewQuickIntake
            ? await quickIntakeService.GetAsync(id, HttpContext.RequestAborted)
            : null;
        return Page();
    }


    public async Task<IActionResult> OnPostVerifyDocumentAsync(Guid id, Guid documentId, string? notes)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        if (await CheckDocumentAsync(id, documentId) is IActionResult error) return error;
        if (notes?.Length > 1000) return BadRequest("Verification notes must be at most 1000 characters.");
        await documentService.VerifyAsync(staffUser.Id, documentId, notes, HttpContext.RequestAborted);
        TempData["Success"] = "Document verified.";
        return RedirectToPage(new { id, selectedDocumentId = documentId });
    }

    public async Task<IActionResult> OnPostRejectDocumentAsync(Guid id, Guid documentId, string reason)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        if (await CheckDocumentAsync(id, documentId) is IActionResult error) return error;
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000) return BadRequest("A rejection reason of at most 1000 characters is required.");
        await documentService.RejectVerificationAsync(staffUser.Id, documentId, reason, HttpContext.RequestAborted);
        TempData["Success"] = "Document verification rejected.";
        return RedirectToPage(new { id, selectedDocumentId = documentId });
    }

    public async Task<IActionResult> OnPostArchiveDocumentAsync(Guid id, Guid documentId)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        if (await CheckDocumentAsync(id, documentId) is IActionResult error) return error;
        await documentService.ArchiveAsync(staffUser.Id, documentId, HttpContext.RequestAborted);
        TempData["Success"] = "Document archived.";
        return RedirectToPage(new { id, selectedDocumentId = (Guid?)null });
    }

    private async Task<IActionResult?> CheckDocumentAsync(Guid id, Guid documentId)
    {
        var status = await documentContextService.ValidateAsync(DocumentOwnerType.AssistanceRequest, id, documentId, HttpContext.RequestAborted);
        return status switch
        {
            StaffDocumentContextStatus.Available => null,
            StaffDocumentContextStatus.Archived => new ObjectResult("This document has been archived and is no longer available for review.") { StatusCode = 410 },
            _ => NotFound("The document or its related request could not be found in this review context.")
        };
    }

    private async Task<ApplicationUser> GetStaffUserAsync() =>
        await userManager.GetUserAsync(User)
        ?? throw new InvalidOperationException("Authenticated FFP staff user could not be resolved.");
}
