using System.Security.Claims;
using Microsoft.Extensions.Options;
using RequestPortal.Infrastructure.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Web.Security;
using RequestPortal.Web.Services.WhatsAppCommissioning;

namespace RequestPortal.Web.Pages.Administration;

[Authorize(Policy = WhatsAppSettingsAdministratorRequirement.Policy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WhatsAppSettingsModel(CommissioningService commissioning, StaffAdministrationService administration, IOptions<NotificationOptions> options) : PageModel
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
    public string SenderId => options.Value.WhatsAppPhoneNumberId;
    public string ApiVersion => options.Value.WhatsAppGraphApiVersion;
    public bool AutomaticDeliveryEnabled => options.Value.WhatsAppEnabled;
    public bool SenderConfigured => SenderId == CommissioningGateway.Phone && options.Value.WhatsAppBusinessAccountId == CommissioningGateway.Account;
    public bool? PermissionVerified => Readiness?.Stage is "permissions" or "template" or "ready" ? Readiness.MessagingPermissionConfirmed : null;
    public bool? TemplateAvailable => Readiness?.Stage is "template" or "ready" ? Readiness.TemplateApproved : null;
    public static string CheckLabel(bool? value) => value switch { true => "Verified", false => "Needs attention", _ => "Not checked" };
    public static string CheckClass(bool? value) => value switch { true => "status status-approved", false => "status status-rejected", _ => "status" };
    public static string StateLabel(string value) => value switch {
        "Authorized" => "Ready to send once", "Attempted" => "Outcome pending — do not resend", "Accepted" => "Accepted by Meta",
        "Rejected" => "Rejected by Meta", "NotSent" => "Not sent", "Unknown" => "Outcome unknown — do not resend",
        "sent" => "Sent", "delivered" => "Delivered", "read" => "Read", "failed" => "Delivery failed", _ => "Not yet confirmed" };
    public static string StateClass(string value) => value switch {
        "Accepted" or "delivered" or "read" => "status status-approved", "Rejected" or "failed" => "status status-rejected",
        "Attempted" or "Unknown" => "status status-submitted", _ => "status" };
    public static string StageLabel(string value) => value switch {
        "ready" => "All connection checks passed", "token" => "Protected credential access", "sender" => "Meta sender verification",
        "permissions" => "Meta messaging permission", "template" => "Template approval", _ => "Connection verification" };
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
