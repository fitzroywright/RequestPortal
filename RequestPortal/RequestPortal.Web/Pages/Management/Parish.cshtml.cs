using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Management;

namespace RequestPortal.Web.Pages.Management;

public sealed class ParishModel : PageModel
{
    private readonly IManagementDashboardService dashboardService;

    public ParishModel(IManagementDashboardService dashboardService)
    {
        this.dashboardService = dashboardService;
    }

    [BindProperty(SupportsGet = true)] public string Parish { get; set; } = string.Empty;

    public ParishDrillDown Data { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(Parish)) return RedirectToPage("/Management/Index");
        Data = await dashboardService.GetParishAsync(Parish, HttpContext.RequestAborted);
        return Page();
    }
}
