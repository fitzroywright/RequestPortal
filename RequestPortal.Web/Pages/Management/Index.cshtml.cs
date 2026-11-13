using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Management;

namespace RequestPortal.Web.Pages.Management;

public sealed class IndexModel : PageModel
{
    private readonly IManagementDashboardService dashboardService;

    public IndexModel(IManagementDashboardService dashboardService)
    {
        this.dashboardService = dashboardService;
    }

    public ManagementDashboardSnapshot Dashboard { get; private set; } = null!;

    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)] public string? Parish { get; set; }

    public bool IsInitializing { get; private set; }

    public async Task OnGetAsync()
    {
        try { Dashboard = string.IsNullOrWhiteSpace(Parish)
            ? await dashboardService.GetDashboardAsync(HttpContext.RequestAborted)
            : await dashboardService.GetDashboardAsync(new ManagementDashboardFilter(Parish: Parish), HttpContext.RequestAborted); }
        catch (ManagementDashboardInitializingException) { IsInitializing = true; }
    }
}
