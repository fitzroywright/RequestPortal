using Common.Security.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Administration;

public sealed class AddStaffModel(StaffPreProvisioningService provisioning, StaffAdministrationService administration) : PageModel
{
    [BindProperty] public string? Query { get; set; }
    [BindProperty] public string? Selection { get; set; }
    [BindProperty] public string[] SelectedRoles { get; set; } = [];
    [BindProperty] public bool Activate { get; set; }
    public string? UnavailableReason => provisioning.UnavailableReason;
    public IReadOnlyList<StaffDirectoryChoice> Matches { get; private set; } = [];
    public DirectoryStaffIdentity? Preview { get; private set; }
    public IReadOnlyCollection<RoleDefinition> Roles { get; private set; } = [];
    public bool Searched { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        try { await LoadAsync(); return Page(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
    public Task<IActionResult> OnPostSearchAsync() => HandleAsync(async () => {
        if (!ModelState.IsValid) return Page();
        Matches=await provisioning.SearchAsync(User,Query ?? "",HttpContext.RequestAborted);
        Searched=true;
        return Page();
    });
    public Task<IActionResult> OnPostReviewAsync() => HandleAsync(async () => {
        if (!ModelState.IsValid) return Page();
        Preview=await provisioning.PreviewAsync(User,Selection ?? "",HttpContext.RequestAborted);
        return Page();
    });
    public Task<IActionResult> OnPostProvisionAsync() => HandleAsync(async () => {
        if (!ModelState.IsValid) return Page();
        var result=await provisioning.ProvisionAsync(User,Selection ?? "",SelectedRoles,Activate,HttpContext.RequestAborted);
        TempData["Success"]=result.Created ? "Staff account provisioned and audited." : "This staff identity already exists. Its roles and access were preserved.";
        return RedirectToPage("/Administration/Staff",new { Id=result.AccountId });
    });
    private async Task LoadAsync() => Roles=await administration.RolesAsync(User);
    private async Task<IActionResult> HandleAsync(Func<Task<IActionResult>> operation)
    {
        try { await LoadAsync(); return await operation(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (InvalidOperationException exception) { ModelState.AddModelError("",exception.Message); return Page(); }
    }
}
