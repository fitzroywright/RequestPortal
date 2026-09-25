using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Reporting;

namespace RequestPortal.Web.Pages.Staff;

public sealed class ReportsModel : PageModel
{
    private readonly IStaffReportingService reportingService;
    private readonly IBusinessAuditWriter auditWriter;

    public ReportsModel(
        IStaffReportingService reportingService,
        IBusinessAuditWriter auditWriter)
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

    public async Task<IActionResult> OnGetExportPdfAsync()
    {
        byte[] content = await reportingService.ExportPdfAsync(BuildFilter(), HttpContext.RequestAborted);
        await AuditExportAsync("PDF");
        return File(content, "application/pdf", $"request-portal-report-{DateTime.UtcNow:yyyyMMdd-HHmm}.pdf");
    }

    public async Task<IActionResult> OnGetPrintAsync()
    {
        string html = await reportingService.ExportPrintableHtmlAsync(BuildFilter(), HttpContext.RequestAborted);
        await AuditExportAsync("PrintableHtml");
        return Content(html, "text/html; charset=utf-8");
    }

    private async Task AuditExportAsync(string format)
    {
        string? actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid actorId = Guid.TryParse(actor, out Guid parsed) ? parsed : Guid.Empty;
        await auditWriter.WriteAsync(
            actorId,
            "ReportExported",
            "RequestReport",
            Guid.Empty,
            null,
            $"format={format}; search={Search}; status={Status}; category={Category}; priority={Priority}; from={FromDate:yyyy-MM-dd}; to={ToDate:yyyy-MM-dd}",
            HttpContext.RequestAborted);
    }

    private RequestReportFilter BuildFilter()
    {
        DateTimeOffset? fromUtc = FromDate.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(FromDate.Value.Date, DateTimeKind.Utc)) : null;
        DateTimeOffset? toUtc = ToDate.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(ToDate.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc)) : null;
        return new RequestReportFilter(Search, Status, CountryCode, Category, Priority, fromUtc, toUtc, PageNumber, 100);
    }
}
