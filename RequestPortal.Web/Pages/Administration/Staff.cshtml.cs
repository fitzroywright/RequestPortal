using Common.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Administration;

public sealed class StaffModel(StaffAdministrationService service) : PageModel
{
    [BindProperty(SupportsGet=true)] public string? Search { get; set; }
    [BindProperty(SupportsGet=true)] public string? Status { get; set; }
    [BindProperty(SupportsGet=true)] public string? Role { get; set; }
    [BindProperty(SupportsGet=true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet=true)] public string? Id { get; set; }
    [BindProperty] public string[] SelectedRoles { get; set; } = [];
    [BindProperty] public string Reason { get; set; } = "";
    [BindProperty] public long Version { get; set; }
    [BindProperty] public string DisplayName { get; set; } = "";
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public bool Confirmed { get; set; }
    public IReadOnlyList<StaffEntry> Entries { get; private set; } = [];
    public IReadOnlyCollection<RoleDefinition> Roles { get; private set; } = [];
    public StaffEntry? Selected { get; private set; }
    public IReadOnlyCollection<string> Permissions { get; private set; } = [];
    public IReadOnlyList<StaffAudit> History { get; private set; } = [];
    public int PendingCount { get; private set; }
    public bool Provisioned { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        try { await LoadAsync(); return Page(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
    public async Task<IActionResult> OnPostAsync(string operation)
    {
        if (!ModelState.IsValid) { await LoadAsync(); return Page(); }
        try {
            if (Id is null) return BadRequest();
            await service.ChangeAsync(User, Id, operation, Reason, SelectedRoles, Version, Confirmed, DisplayName, Email);
            TempData["Success"] = "Staff access decision saved and audited.";
            return RedirectToPage(new { Id });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException exception) { ModelState.AddModelError("",exception.Message); await LoadAsync(); return Page(); }
    }
    private async Task LoadAsync()
    {
        var all = await service.DirectoryAsync(User);
        Roles = await service.RolesAsync(User);
        PendingCount = all.Count(x => x.Account.Status == global::Common.Security.Abstractions.SecurityAccountStatus.PendingAccess);
        Provisioned = await service.IsProvisionedAsync();
        if (Id is not null) {
            Selected = all.SingleOrDefault(x => x.Account.Id == Id);
            if (Selected is not null) {
                if (Request.Method == "GET") { SelectedRoles = Selected.Roles.ToArray(); Version = Selected.Account.SessionVersion; DisplayName = Selected.Account.DisplayName; Email = Selected.Email; }
                Permissions = await service.PermissionsAsync(User,Id);
                History = await service.HistoryAsync(User,Id);
            }
        }
        var filtered = all.Where(x => (string.IsNullOrWhiteSpace(Search) ||
            $"{x.Account.DisplayName} {x.Account.LoginName} {x.Email} {string.Join(' ',x.Identities.Select(i=>i.Key.ProviderId))}".Contains(Search,StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrEmpty(Status) || x.Account.Status.ToString() == Status)
            && (string.IsNullOrEmpty(Role) || x.Roles.Contains(Role)));
        Entries = (Sort switch {
            "username" => filtered.OrderBy(x=>x.Account.LoginName),
            "email" => filtered.OrderBy(x=>x.Email),
            "provider" => filtered.OrderBy(x=>x.Account.ProviderKind),
            "status" => filtered.OrderBy(x=>x.Account.Status),
            "last" => filtered.OrderByDescending(x=>x.LastSignIn),
            "registered" => filtered.OrderByDescending(x=>x.Registered),
            _ => filtered.OrderBy(x=>x.Account.DisplayName)
        }).ToArray();
    }
}
