using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Management;

namespace RequestPortal.Web.Pages.Management;

public sealed class RequestsModel : PageModel
{
    private readonly IManagementDashboardService dashboardService;

    public RequestsModel(IManagementDashboardService dashboardService)
    {
        this.dashboardService = dashboardService;
    }

    [BindProperty(SupportsGet = true)] public string? Period { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Attention { get; set; }
    [BindProperty(SupportsGet = true)] public string? Category { get; set; }
    [BindProperty(SupportsGet = true)] public string? Program { get; set; }
    [BindProperty(SupportsGet = true)] public string? Parish { get; set; }
    [BindProperty(SupportsGet = true)] public string? Community { get; set; }

    public IReadOnlyList<ManagementRequestRow> Requests { get; private set; } = Array.Empty<ManagementRequestRow>();

    public async Task OnGetAsync()
    {
        Requests = await dashboardService.GetRequestsAsync(
            new ManagementRequestFilter(Period, Status, Attention, Category, Program, Parish, Community),
            HttpContext.RequestAborted);
    }
}
