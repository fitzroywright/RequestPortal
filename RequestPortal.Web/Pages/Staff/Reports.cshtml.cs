using System;
using System.Threading.Tasks;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Reporting;
using RequestPortal.Application.Auditing;

namespace RequestPortal.Web.Pages.Staff;

public sealed class ReportsModel : PageModel
{
    private readonly IStaffReportingService reportingService;
    private readonly IBusinessAuditWriter auditWriter;

    public ReportsModel(IStaffReportingService reportingService, IBusinessAuditWriter auditWriter)
    {
        this.reportingService = reportingService;
        this.auditWriter = auditWriter;
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
        await AuditExportAsync("CSV");
        return File(content, "text/csv; charset=utf-8", $"request-portal-report-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
    }

    public async Task<IActionResult> OnGetExportXlsxAsync()
    {
        byte[] content = await reportingService.ExportXlsxAsync(BuildFilter(), HttpContext.RequestAborted);
        await AuditExportAsync("XLSX");
        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"request-portal-report-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
    }

    public async Task<IActionResult> OnGetPrintAsync()
    {
        string html = await reportingService.ExportPrintableHtmlAsync(BuildFilter(), HttpContext.RequestAborted);
        await AuditExportAsync("PrintableHtml");
        return Content(html, "text/html; charset=utf-8");
    }

    private Task AuditExportAsync(string format)
    {
        Guid actor = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid id) ? id : Guid.Empty;
        return auditWriter.WriteAsync(
            actor,
            "ReportExported",
            "ManagementReport",
            Guid.NewGuid(),
            null,
            $"Format={format}; Search={Search}; Status={Status}; Category={Category}; Priority={Priority}; From={FromDate:O}; To={ToDate:O}",
            HttpContext.RequestAborted);
    }

    private RequestReportFilter BuildFilter()
    {
        DateTimeOffset? fromUtc = FromDate.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(FromDate.Value.Date, DateTimeKind.Utc)) : null;
        DateTimeOffset? toUtc = ToDate.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(ToDate.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc)) : null;
        return new RequestReportFilter(Search, Status, CountryCode, Category, Priority, fromUtc, toUtc, PageNumber, 100);
    }
}
