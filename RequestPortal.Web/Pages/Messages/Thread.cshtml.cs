using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Messages;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Messages;

public sealed class ThreadModel(
    IPortalMessageService messageService,
    IDocumentService documentService) : PageModel
{
    public MessageThreadView? Thread { get; private set; }

    public IReadOnlyList<DocumentSummary> AvailableAttachments { get; private set; } = Array.Empty<DocumentSummary>();

    public bool IsStaff { get; private set; }

    public bool IsAssignedToCurrentStaff { get; private set; }

    [BindProperty]
    [Required]
    [StringLength(10000)]
    public string Reply { get; set; } = string.Empty;

    [BindProperty]
    public List<Guid> AttachmentDocumentIds { get; set; } = [];

    public Task<IActionResult> OnGetAsync(Guid id) => LoadAsync(id);

    public async Task<IActionResult> OnGetAttachmentAsync(Guid id, Guid documentId)
    {
        Guid userId = CurrentUserId();
        IsStaff = IsFfpStaff();

        bool allowed = await messageService.CanAccessAttachmentAsync(
            userId,
            IsStaff,
            id,
            documentId,
            HttpContext.RequestAborted);

        if (!allowed)
        {
            return NotFound();
        }

        DocumentDownload? download = await documentService.DownloadAsync(
            userId,
            documentId,
            IsStaff,
            HttpContext.RequestAborted);

        if (download is null)
        {
            return NotFound();
        }

        return IsInlinePreviewType(download.ContentType)
            ? File(download.Content, download.ContentType)
            : File(download.Content, download.ContentType, download.FileName);
    }

    private static bool IsInlinePreviewType(string contentType) =>
        contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase);

    public async Task<IActionResult> OnPostAssignAsync(Guid id)
    {
        Guid userId = CurrentUserId();
        if (!IsFfpStaff())
        {
            return Forbid();
        }

        await messageService.AssignThreadAsync(
            userId,
            id,
            HttpContext.RequestAborted);

        TempData["Success"] = "Conversation assigned to you.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUnassignAsync(Guid id)
    {
        Guid userId = CurrentUserId();
        if (!IsFfpStaff())
        {
            return Forbid();
        }

        await messageService.UnassignThreadAsync(
            userId,
            id,
            HttpContext.RequestAborted);

        TempData["Success"] = "Conversation returned to the unassigned queue.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCloseAsync(Guid id)
    {
        Guid userId = CurrentUserId();
        if (!IsFfpStaff())
        {
            return Forbid();
        }

        await messageService.CloseThreadAsync(
            userId,
            id,
            HttpContext.RequestAborted);

        TempData["Success"] = "Conversation closed.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostReopenAsync(Guid id)
    {
        Guid userId = CurrentUserId();
        if (!IsFfpStaff())
        {
            return Forbid();
        }

        await messageService.ReopenThreadAsync(
            userId,
            id,
            HttpContext.RequestAborted);

        TempData["Success"] = "Conversation reopened.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        Guid userId = CurrentUserId();
        IsStaff = IsFfpStaff();

        if (!ModelState.IsValid)
        {
            return await LoadAsync(id);
        }

        await messageService.ReplyAsync(
            userId,
            IsStaff,
            id,
            Reply,
            cancellationToken: HttpContext.RequestAborted,
            attachmentDocumentIds: AttachmentDocumentIds);

        return RedirectToPage(new { id });
    }

    private async Task<IActionResult> LoadAsync(Guid id)
    {
        Guid userId = CurrentUserId();
        IsStaff = IsFfpStaff();

        Thread = await messageService.GetThreadAsync(
            userId,
            IsStaff,
            id,
            HttpContext.RequestAborted);

        if (Thread is null)
        {
            return NotFound();
        }

        IsAssignedToCurrentStaff =
            IsStaff &&
            Thread.AssignedToStaffUserId == userId;

        if (Thread.RequestId.HasValue)
        {
            AvailableAttachments = await documentService.GetRequestDocumentsAsync(
                userId,
                Thread.RequestId.Value,
                IsStaff,
                HttpContext.RequestAborted);
        }

        return Page();
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user ID is unavailable."));

    private bool IsFfpStaff() =>
        User.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);
}
