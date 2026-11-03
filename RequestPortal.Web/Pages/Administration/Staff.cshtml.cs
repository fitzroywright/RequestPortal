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
        try {
            if (!ModelState.IsValid) { await LoadAsync(); return Page(); }
            if (Id is null) return BadRequest();
            await service.ChangeAsync(User, Id, operation, SelectedRoles, Version, Confirmed, DisplayName, Email);
            TempData["Success"] = "Staff access decision saved and audited.";
            return RedirectToPage(new { Id });
        }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException exception) { ModelState.AddModelError("",exception.Message); await LoadAsync(); return Page(); }
    }
    public static string DisplayTime(DateTimeOffset? value) => value.HasValue
        ? TimeZoneInfo.ConvertTime(value.Value,TimeZoneInfo.FindSystemTimeZoneById("America/Jamaica")).ToString("dd MMM yyyy, h:mm tt",System.Globalization.CultureInfo.InvariantCulture) : "—";
    public static string DisplayAuditTime(string value) => DateTimeOffset.TryParse(value,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal,out var time) ? DisplayTime(time) : "Unknown time";
    public static string StatusLabel(global::Common.Security.Abstractions.SecurityAccountStatus value) => value switch {
        global::Common.Security.Abstractions.SecurityAccountStatus.PendingAccess => "Pending access",
        global::Common.Security.Abstractions.SecurityAccountStatus.Disabled => "Access revoked", _ => value.ToString() };
    public static string ActionLabel(string value) => value.Split(':',2)[0] switch {
        "Save" => "Staff details or roles updated", "Approve" => "Access approved", "Activate" => "Access activated",
        "Suspend" => "Account suspended", "Revoke" => "Access revoked", "Reject" => "Access rejected",
        "PreProvisioned" => "Staff provisioned before first sign-in",
        "Bootstrap" => "Initial administrator provisioned", "RolesConsolidated" => "Legacy roles consolidated",
        "StaffAuthenticated" => "Signed in", "ApplicationAuthorizationChanged" => "Authorization refreshed",
        "AccountStatus" => "Account status changed", "SessionsRevoked" => "Sessions revoked",
        "AccountAdministrativelyProvisioned" => "Staff account provisioned", "ApprovedIdentityMigrationImported" => "Verified identity imported",
        "IdentityLinked" => "Sign-in method linked", "IdentityUnlinked" => "Sign-in method removed",
        "AccountAutomaticallyProvisioned" => "Staff account registered", _ => value };
    public string PermissionSummary
    {
        get {
            if (Selected?.Account.Status != global::Common.Security.Abstractions.SecurityAccountStatus.Active) return "No active access.";
            var abilities = new List<string>();
            if (Permissions.Contains(RequestPortal.Infrastructure.Security.RequestPortalPermissions.ManageSecurity)) abilities.Add("manage staff access");
            if (Permissions.Contains(RequestPortal.Infrastructure.Security.RequestPortalPermissions.ReviewRequests)) abilities.Add("review requests");
            if (Permissions.Contains(RequestPortal.Infrastructure.Security.RequestPortalPermissions.ViewManagementDashboard)) abilities.Add("view management dashboards");
            if (abilities.Count==0 && Permissions.Contains(RequestPortal.Infrastructure.Security.RequestPortalPermissions.Access)) abilities.Add("access the staff workspace");
            return abilities.Count==0 ? (Permissions.Count==0?"No active permissions.":"Has additional assigned permissions.") : "Can "+string.Join(", ",abilities)+".";
        }
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
