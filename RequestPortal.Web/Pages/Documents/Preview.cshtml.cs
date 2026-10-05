using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Messages;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Documents;

public sealed class PreviewModel(
    IDocumentService documentService,
    IDocumentPreviewRenderer previewRenderer,
    IPortalMessageService messageService) : PageModel
{
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = "application/octet-stream";
    public string? HtmlPreview { get; private set; }
    public bool IsPdf { get; private set; }
    public bool IsImage { get; private set; }
    public bool HasPreview => IsPdf || IsImage || !string.IsNullOrWhiteSpace(HtmlPreview);

    public async Task<IActionResult> OnGetAsync(Guid documentId, Guid? requestId, Guid? threadId)
    {
        if (!await CanAccessAsync(documentId, requestId, threadId))
        {
            return NotFound();
        }

        DocumentDownload? download = await documentService.DownloadAsync(
            CurrentUserId(),
            documentId,
            IsFfpStaff(),
            HttpContext.RequestAborted);

        if (download is null)
        {
            return NotFound();
        }

        await using (download.Content)
        {
            FileName = download.FileName;
            ContentType = download.ContentType;
            IsPdf = ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);
            IsImage = ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

            if (!IsPdf && !IsImage)
            {
                try
                {
                    HtmlPreview = await previewRenderer.RenderHtmlAsync(
                        FileName,
                        ContentType,
                        download.Content,
                        HttpContext.RequestAborted);
                }
                catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    ILogger logger = HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("RequestPortal.Documents.Preview");

                    logger.LogWarning(
                        exception,
                        "Document preview generation failed for {FileName}; falling back to download-only behavior. CorrelationId={CorrelationId}",
                        FileName,
                        HttpContext.TraceIdentifier);

                    HtmlPreview = null;
                }
            }
        }

        ViewData["Title"] = FileName;
        return Page();
    }

    public async Task<IActionResult> OnGetRawAsync(Guid documentId, Guid? requestId, Guid? threadId)
    {
        if (!await CanAccessAsync(documentId, requestId, threadId))
        {
            return NotFound();
        }

        DocumentDownload? download = await documentService.DownloadAsync(
            CurrentUserId(),
            documentId,
            IsFfpStaff(),
            HttpContext.RequestAborted);

        return download is null
            ? NotFound()
            : File(download.Content, download.ContentType);
    }

    public async Task<IActionResult> OnGetDownloadAsync(Guid documentId, Guid? requestId, Guid? threadId)
    {
        if (!await CanAccessAsync(documentId, requestId, threadId))
        {
            return NotFound();
        }

        DocumentDownload? download = await documentService.DownloadAsync(
            CurrentUserId(),
            documentId,
            IsFfpStaff(),
            HttpContext.RequestAborted);

        return download is null
            ? NotFound()
            : File(download.Content, download.ContentType, download.FileName);
    }

    private async Task<bool> CanAccessAsync(Guid documentId, Guid? requestId, Guid? threadId)
    {
        Guid userId = CurrentUserId();
        bool isStaff = IsFfpStaff();

        if (threadId.HasValue)
        {
            return await messageService.CanAccessAttachmentAsync(
                userId,
                isStaff,
                threadId.Value,
                documentId,
                HttpContext.RequestAborted);
        }

        if (requestId.HasValue)
        {
            IReadOnlyList<DocumentSummary> documents =
                await documentService.GetRequestDocumentsAsync(
                    userId,
                    requestId.Value,
                    isStaff,
                    HttpContext.RequestAborted);

            return documents.Any(document => document.Id == documentId);
        }

        return false;
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user ID is unavailable."));

    private bool IsFfpStaff() =>
        User.HasClaim(
            StaffOidcOptions.StaffSurfaceClaim,
            StaffOidcOptions.StaffSurfaceValue);
}
