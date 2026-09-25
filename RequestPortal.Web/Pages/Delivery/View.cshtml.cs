using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Delivery;

namespace RequestPortal.Web.Pages.Delivery;

public sealed class ViewModel : PageModel
{
    private readonly IDeliveryService deliveryService;

    public ViewModel(IDeliveryService deliveryService)
    {
        this.deliveryService = deliveryService;
    }

    public DeliveryView? Delivery { get; private set; }
    public IReadOnlyList<DeliveryLineSnapshot> Lines { get; private set; } = Array.Empty<DeliveryLineSnapshot>();
    public string? SourceDocumentSummary { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    [BindProperty]
    public string AcknowledgedBy { get; set; } = string.Empty;

    [BindProperty]
    public string Status { get; set; } = "Delivered";

    [BindProperty]
    public string SignatureData { get; set; } = string.Empty;

    [BindProperty]
    public string SignatureText { get; set; } = string.Empty;

    [BindProperty]
    public IFormFile? EvidenceFile { get; set; }

    [BindProperty]
    public string? Notes { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid deliveryId)
    {
        Delivery = await deliveryService.GetAsync(deliveryId, Token, HttpContext.RequestAborted);
        if (Delivery is null) return Page();
        ReadSnapshot(Delivery.SnapshotJson);
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
            ModelState.AddModelError(nameof(AcknowledgedBy), "Enter the name of the person acknowledging the delivery.");
        }

        string acknowledgement = string.IsNullOrWhiteSpace(SignatureData)
            ? SignatureText.Trim()
            : SignatureData;

        if (string.IsNullOrWhiteSpace(acknowledgement))
        {
            ModelState.AddModelError(nameof(SignatureText), "Type your name or sign in the signature box.");
        }

        if (!ModelState.IsValid)
        {
            ReadSnapshot(Delivery.SnapshotJson);
            return Page();
        }

        Guid? evidenceDocumentId = null;
        if (EvidenceFile is not null && EvidenceFile.Length > 0)
        {
            await using Stream content = EvidenceFile.OpenReadStream();
            evidenceDocumentId = await deliveryService.UploadEvidenceAsync(
                deliveryId,
                Token,
                EvidenceFile.FileName,
                EvidenceFile.ContentType ?? "application/octet-stream",
                EvidenceFile.Length,
                content,
                HttpContext.RequestAborted);
        }

        await deliveryService.AcknowledgeAsync(
            deliveryId,
            Token,
            Status,
            AcknowledgedBy,
            acknowledgement,
            evidenceDocumentId,
            Notes,
            HttpContext.RequestAborted);

        return RedirectToPage(new { deliveryId, token = Token });
    }

    private void ReadSnapshot(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("Lines", out JsonElement lines) && lines.ValueKind == JsonValueKind.Array)
        {
            Lines = lines.EnumerateArray()
                .Select(line => new DeliveryLineSnapshot(
                    line.TryGetProperty("Description", out JsonElement description) ? description.GetString() ?? string.Empty : string.Empty,
                    line.TryGetProperty("Quantity", out JsonElement quantity) && quantity.TryGetDecimal(out decimal value) ? value : 0m,
                    line.TryGetProperty("UnitOfMeasure", out JsonElement unit) ? unit.GetString() : null))
                .ToArray();
        }

        if (root.TryGetProperty("DocumentPayload", out JsonElement payload) &&
            payload.ValueKind != JsonValueKind.Null &&
            payload.ValueKind != JsonValueKind.Undefined)
        {
            SourceDocumentSummary = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
