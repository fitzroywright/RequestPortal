using System.Text.Json;
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
    public string FormattedSnapshot { get; private set; } = string.Empty;

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

    public async Task<IActionResult> OnGetAsync(Guid deliveryId)
    {
        Delivery = await deliveryService.GetAsync(deliveryId, Token, HttpContext.RequestAborted);
        if (Delivery is null) return Page();
        FormattedSnapshot = FormatJson(Delivery.SnapshotJson);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid deliveryId)
    {
        await deliveryService.AcknowledgeAsync(
            deliveryId,
            Token,
            Status,
            AcknowledgedBy,
            SignatureData,
            null,
            Notes,
            HttpContext.RequestAborted);

        return RedirectToPage(new { deliveryId, token = Token });
    }

    private static string FormatJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }
}
