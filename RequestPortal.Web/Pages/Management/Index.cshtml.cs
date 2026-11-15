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
    public IReadOnlyList<ParishReachSummary> MapParishes { get; private set; } = [];

    [Microsoft.AspNetCore.Mvc.BindProperty(SupportsGet = true)] public string? Parish { get; set; }

    public bool IsInitializing { get; private set; }

    public async Task OnGetAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Parish))
            {
                Dashboard = await dashboardService.GetDashboardAsync(HttpContext.RequestAborted);
                MapParishes = Dashboard.Parishes;
            }
            else
            {
                // Map coloration always uses nationwide actuals, not the filtered dashboard snapshot.
                var nationwide = await dashboardService.GetDashboardAsync(HttpContext.RequestAborted);
                MapParishes = nationwide.Parishes;
                Dashboard = await dashboardService.GetDashboardAsync(new ManagementDashboardFilter(Parish: Parish), HttpContext.RequestAborted);
            }
        }
        catch (ManagementDashboardInitializingException) { IsInitializing = true; }
    }
}
