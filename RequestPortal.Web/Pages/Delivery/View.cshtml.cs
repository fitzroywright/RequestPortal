using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Delivery;

namespace RequestPortal.Web.Pages.Delivery;

public sealed class ViewModel : PageModel
{
    private const long MaxEvidenceBytes = 10L * 1024L * 1024L;
    private readonly IDeliveryService deliveryService;

    public ViewModel(IDeliveryService deliveryService)
    {
        this.deliveryService = deliveryService;
    }

    public DeliveryView? Delivery { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    public string AcknowledgedBy { get; set; } = string.Empty;

    [BindProperty]
    public string Status { get; set; } = "Delivered";

    [BindProperty]
    public string SignatureData { get; set; } = string.Empty;

    [BindProperty]
    public string? Notes { get; set; }

    [BindProperty]
    public IFormFile? Evidence { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid deliveryId)
    {
        Delivery = await deliveryService.GetAsync(deliveryId, Token, HttpContext.RequestAborted);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid deliveryId)
    {
        Delivery = await deliveryService.GetAsync(deliveryId, Token, HttpContext.RequestAborted);
        if (Delivery is null)
        {
            return Page();
        }

        if (string.IsNullOrWhiteSpace(AcknowledgedBy))
        {
            ModelState.AddModelError(nameof(AcknowledgedBy), "Enter the name of the person acknowledging this delivery.");
        }
        if (string.IsNullOrWhiteSpace(SignatureData))
        {
            ModelState.AddModelError(nameof(SignatureData), "Provide a signature or typed acknowledgement.");
        }
        if (Evidence is not null && Evidence.Length > MaxEvidenceBytes)
        {
            ModelState.AddModelError(nameof(Evidence), "Delivery evidence must be 10 MB or smaller.");
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }

        Guid? evidenceDocumentId = null;
        if (Evidence is not null && Evidence.Length > 0)
        {
            await using Stream content = Evidence.OpenReadStream();
            evidenceDocumentId = await deliveryService.UploadEvidenceAsync(
                deliveryId,
                Token,
                Evidence.FileName,
                Evidence.ContentType ?? "application/octet-stream",
                Evidence.Length,
                content,
                HttpContext.RequestAborted);
        }

        await deliveryService.AcknowledgeAsync(
            deliveryId,
            Token,
            Status,
            AcknowledgedBy,
            SignatureData,
            evidenceDocumentId,
            Notes,
            HttpContext.RequestAborted);

        return RedirectToPage(new { deliveryId, token = Token });
    }
}
