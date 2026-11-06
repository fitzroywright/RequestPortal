using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Administration;

// Preserve existing bookmarks and in-flight forms without exposing an older,
// less restrictive endpoint. The destination enforces the same role and consent.
[Authorize(Policy = WhatsAppSettingsAdministratorRequirement.Policy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WhatsAppCommissioningModel : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OperationId { get; set; }
    public IActionResult OnGet() => RedirectToPage("/Administration/WhatsAppSettings", new { OperationId });
    public IActionResult OnPostCheck() => Forward("Check");
    public IActionResult OnPostAuthorize() => Forward("Authorize");
    public IActionResult OnPostSend() => Forward("Send");
    private IActionResult Forward(string handler) => RedirectToPagePreserveMethod("/Administration/WhatsAppSettings", handler, new { OperationId });
}
