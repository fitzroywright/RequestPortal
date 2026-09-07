using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Portal;
using RequestPortal.Application.Requests;
using RequestPortal.Domain.Documents;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Requests;

public sealed class DetailsModel : PageModel
{
    private static readonly Guid eachUnitId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly IDocumentService documentService;
    private readonly IRequestLifecycleService lifecycleService;

    public DetailsModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IDocumentService documentService,
        IRequestLifecycleService lifecycleService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.documentService = documentService;
        this.lifecycleService = lifecycleService;
    }

    public new RequestDetail? Request { get; private set; }

    public IReadOnlyList<DocumentSummary> Documents { get; private set; } = Array.Empty<DocumentSummary>();

    [BindProperty]
    public ItemInput Item { get; set; } = new();

    [BindProperty]
    public DocumentInput Document { get; set; } = new();

    [BindProperty]
    public CancelInput Cancellation { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        return await LoadPageAsync(user, id);
    }

    public async Task<IActionResult> OnPostAddItemAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();

        if (!ModelState.IsValid)
        {
            return await LoadPageAsync(user, id);
        }

        await workflowService.AddRequestItemAsync(
            user.Id,
            id,
            Item.Description,
            Item.Quantity,
            eachUnitId,
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

    private async Task<IActionResult> LoadPageAsync(ApplicationUser user, Guid id)
    {
        Request = await workflowService.GetRequestAsync(user.Id, id, isStaff: false, HttpContext.RequestAborted);
        if (Request is null)
        {
            return NotFound();
        }

        Documents = await documentService.GetRequestDocumentsAsync(user.Id, id, isStaff: false, HttpContext.RequestAborted);
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
}
