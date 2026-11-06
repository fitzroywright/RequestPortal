using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Web.Services;

namespace RequestPortal.Web.Pages.Administration;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NotificationRoutingModel(NotificationRoutingAdministration service) : PageModel
{
    [BindProperty] public Guid StaffUserId { get; set; }
    [BindProperty] public bool Intake { get; set; }
    [BindProperty] public bool Operations { get; set; }
    [BindProperty] public string? Version { get; set; }
    public IReadOnlyList<NotificationRoutingEntry> Entries { get; private set; } = [];
    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        try { Entries = await service.ListAsync(User, ct); return Page(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            if (ModelState.IsValid)
            {
                await service.SaveAsync(User, StaffUserId, Intake, Operations, Version ?? "", ct);
                TempData["Success"] = "Notification responsibilities saved and audited.";
                return RedirectToPage();
            }
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException error) { ModelState.AddModelError("", error.Message); }
        return await OnGetAsync(ct);
    }
}
