using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Web.Security;
using RequestPortal.Web.Services.WhatsAppCommissioning;

namespace RequestPortal.Web.Pages.Administration;

[Authorize(Policy = "StaffAdministration")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WhatsAppCommissioningModel(CommissioningService commissioning, StaffAdministrationService administration) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid? OperationId { get; set; }
    [BindProperty] public string Recipient { get; set; } = "";
    [BindProperty] public string RepeatRecipient { get; set; } = "";
    [BindProperty] public bool VerifiedInMeta { get; set; }
    [BindProperty] public bool AuthorizeRecipient { get; set; }
    [BindProperty] public string Confirmation { get; set; } = "";
    public WhatsAppTestReadiness? Readiness { get; private set; }
    public WhatsAppTestStatus? Test { get; private set; }
    public string? Alert { get; private set; }
    private Guid Actor => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => await Run(async () => {
        if (OperationId is { } id) Test = await commissioning.StatusAsync(Actor, id, ct);
        return Page();
    });
    public async Task<IActionResult> OnPostCheckAsync(CancellationToken ct) => await Run(async () => {
        Readiness = await commissioning.InspectAsync(Actor, ct);
        return Page();
    });
    public async Task<IActionResult> OnPostAuthorizeAsync(CancellationToken ct) => await Run(async () => {
        var id = await commissioning.AuthorizeAsync(Actor, Recipient, RepeatRecipient, VerifiedInMeta, AuthorizeRecipient, ct);
        return RedirectToPage(new { OperationId = id });
    });
    public async Task<IActionResult> OnPostSendAsync(CancellationToken ct) => await Run(async () => {
        if (OperationId is null) return BadRequest();
        await commissioning.SendAsync(Actor, OperationId.Value, Confirmation, ct);
        return RedirectToPage(new { OperationId });
    });
    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        try
        {
            // Re-evaluate active staff account and current administrator grants on every action.
            await administration.RequireAdministratorAsync(User);
            return await action();
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (Exception)
        {
            // No raw Meta/database/provider exception text, bound recipients or secrets.
            ModelState.Clear();
            Alert = "The action was not completed. Check readiness and the operation status. An attempted authorization cannot be resent.";
            return Page();
        }
        finally { Recipient = RepeatRecipient = ""; }
    }
}
