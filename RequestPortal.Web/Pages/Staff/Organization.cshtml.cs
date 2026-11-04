using RequestPortal.Domain.Documents;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Documents;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Staff;

public sealed class OrganizationModel : PageModel
{
    private readonly IStaffOrganizationService organizationService;
    private readonly IStaffDocumentContextService documentContextService;
    private readonly IDocumentService documentService;
    private readonly UserManager<ApplicationUser> userManager;

    public OrganizationModel(
        IStaffOrganizationService organizationService,
        IDocumentService documentService,
        UserManager<ApplicationUser> userManager,
        IStaffDocumentContextService documentContextService)
    {
        this.organizationService = organizationService;
        this.documentService = documentService;
        this.documentContextService = documentContextService;
        this.userManager = userManager;
    }

    public StaffOrganizationDetail? Organization { get; private set; }

    public IReadOnlyList<DocumentSummary> Documents { get; private set; } = Array.Empty<DocumentSummary>();

    [BindProperty(SupportsGet = true)]
    public Guid? SelectedDocumentId { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        return await LoadAsync(id);
    }

    public async Task<IActionResult> OnGetDownloadDocumentAsync(Guid id, Guid documentId)
    {
        ApplicationUser user = await GetUserAsync();
        if (await CheckDocumentAsync(id, documentId) is IActionResult error) return error;
        DocumentDownload? download = await documentService.DownloadAsync(
            user.Id,
            documentId,
            true,
            HttpContext.RequestAborted);

        if (download is null)
        {
            return NotFound();
        }

        return File(download.Content, download.ContentType, download.FileName);
    }

    public async Task<IActionResult> OnPostVerifyDocumentAsync(Guid id, Guid documentId, string? notes)
    {
        ApplicationUser user = await GetUserAsync();
        if (await CheckDocumentAsync(id, documentId) is IActionResult error) return error;
        await documentService.VerifyAsync(user.Id, documentId, notes, HttpContext.RequestAborted);
        TempData["Success"] = "Document verified.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRejectDocumentAsync(Guid id, Guid documentId, string reason)
    {
        ApplicationUser user = await GetUserAsync();
        if (await CheckDocumentAsync(id, documentId) is IActionResult error) return error;
        await documentService.RejectVerificationAsync(user.Id, documentId, reason, HttpContext.RequestAborted);
        TempData["Success"] = "Document verification rejected.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostArchiveDocumentAsync(Guid id, Guid documentId)
    {
        ApplicationUser user = await GetUserAsync();
        if (await CheckDocumentAsync(id, documentId) is IActionResult error) return error;
        await documentService.ArchiveAsync(user.Id, documentId, HttpContext.RequestAborted);
        TempData["Success"] = "Document archived.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRejectOrganizationAsync(Guid id, string reason)
    {
        ApplicationUser user = await GetUserAsync();
        await organizationService.RejectAsync(user.Id, id, reason, HttpContext.RequestAborted);
        TempData["Success"] = "Organization registration rejected.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostSuspendOrganizationAsync(Guid id, string reason)
    {
        ApplicationUser user = await GetUserAsync();
        await organizationService.SuspendAsync(user.Id, id, reason, HttpContext.RequestAborted);
        TempData["Success"] = "Organization suspended.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostReactivateOrganizationAsync(Guid id, string? notes)
    {
        ApplicationUser user = await GetUserAsync();
        await organizationService.ReactivateAsync(user.Id, id, notes, HttpContext.RequestAborted);
        TempData["Success"] = "Organization reactivated.";
        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> LoadAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        Organization = await organizationService.GetAsync(id, HttpContext.RequestAborted);
        if (Organization is null)
        {
            return NotFound();
        }

        if (SelectedDocumentId is Guid selectedId && await CheckDocumentAsync(id, selectedId) is IActionResult error)
            return error;

        Documents = await documentService.GetOrganizationDocumentsAsync(
            user.Id,
            id,
            true,
            HttpContext.RequestAborted);

        return Page();
    }


    private async Task<IActionResult?> CheckDocumentAsync(Guid id, Guid documentId)
    {
        var status = await documentContextService.ValidateAsync(DocumentOwnerType.Organization, id, documentId, HttpContext.RequestAborted);
        return status switch
        {
            StaffDocumentContextStatus.Available => null,
            StaffDocumentContextStatus.Archived => new ObjectResult("This document has been archived and is no longer available for review.") { StatusCode = 410 },
            _ => NotFound("The document or its related organization could not be found in this review context.")
        };
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated FFP staff user could not be resolved.");
    }
}
