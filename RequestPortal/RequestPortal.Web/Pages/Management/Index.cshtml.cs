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

    public async Task OnGetAsync()
    {
        Dashboard = await dashboardService.GetDashboardAsync(HttpContext.RequestAborted);
    }
}
