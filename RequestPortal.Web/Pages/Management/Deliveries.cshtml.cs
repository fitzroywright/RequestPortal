using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Management;

namespace RequestPortal.Web.Pages.Management;

public sealed class DeliveriesModel : PageModel
{
    private readonly IManagementDashboardService dashboardService;

    public DeliveriesModel(IManagementDashboardService dashboardService)
    {
        this.dashboardService = dashboardService;
    }

    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Organization { get; set; }
    [BindProperty(SupportsGet = true)] public string? Program { get; set; }
    [BindProperty(SupportsGet = true)] public string? Parish { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }

    public IReadOnlyList<ManagementDeliveryRow> Deliveries { get; private set; } =
        Array.Empty<ManagementDeliveryRow>();

    public async Task OnGetAsync()
    {
        DateTimeOffset? from = FromDate.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(FromDate.Value.Date, DateTimeKind.Utc))
            : null;
        DateTimeOffset? to = ToDate.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(ToDate.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc))
            : null;

        Deliveries = await dashboardService.GetDeliveriesAsync(
            new ManagementDeliveryFilter(Status, Organization, Program, Parish, from, to),
            HttpContext.RequestAborted);
    }
}
