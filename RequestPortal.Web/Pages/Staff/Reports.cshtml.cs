using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Reporting;

namespace RequestPortal.Web.Pages.Staff;

public sealed class ReportsModel : PageModel
{
    private readonly IStaffReportingService reportingService;

    public ReportsModel(IStaffReportingService reportingService)
    {
        this.reportingService = reportingService;
    }

    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? CountryCode { get; set; }
    [BindProperty(SupportsGet = true)] public string? Category { get; set; }
    [BindProperty(SupportsGet = true)] public string? Priority { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
    [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;

    public RequestReportResult Report { get; private set; } = new(Array.Empty<RequestReportRow>(), 0, 0, 0, 0, 0);

    public async Task OnGetAsync()
    {
        Report = await reportingService.SearchRequestsAsync(BuildFilter(), HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        byte[] content = await reportingService.ExportCsvAsync(BuildFilter(), HttpContext.RequestAborted);
        return File(content, "text/csv; charset=utf-8", $"request-portal-report-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    public async Task<IActionResult> OnGetExportXlsxAsync()
    {
        byte[] content = await reportingService.ExportXlsxAsync(BuildFilter(), HttpContext.RequestAborted);
        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"request-portal-report-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
    }

    public async Task<IActionResult> OnGetPrintAsync()
    {
        string html = await reportingService.ExportPrintableHtmlAsync(BuildFilter(), HttpContext.RequestAborted);
        return Content(html, "text/html; charset=utf-8");
    }

    private RequestReportFilter BuildFilter()
    {
        DateTimeOffset? fromUtc = FromDate.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(FromDate.Value.Date, DateTimeKind.Utc)) : null;
        DateTimeOffset? toUtc = ToDate.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(ToDate.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc)) : null;
        return new RequestReportFilter(Search, Status, CountryCode, Category, Priority, fromUtc, toUtc, PageNumber, 100);
    }
}
