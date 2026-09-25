using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Reporting;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Reporting;

public sealed class StaffReportingService : IStaffReportingService
{
    private readonly RequestPortalDbContext dbContext;

    public StaffReportingService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<RequestReportResult> SearchRequestsAsync(
        RequestReportFilter filter,
        CancellationToken cancellationToken = default)
    {
        IQueryable<AssistanceRequest> query = BuildQuery(filter);
        int total = await query.CountAsync(cancellationToken);
        int pageSize = Math.Clamp(filter.PageSize, 1, 500);
        int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        int page = Math.Clamp(filter.Page, 1, totalPages);

        int draft = await query.CountAsync(item => item.Status == RequestStatus.Draft, cancellationToken);
        int submitted = await query.CountAsync(item => item.Status == RequestStatus.Submitted, cancellationToken);
        int approved = await query.CountAsync(item => item.Status == RequestStatus.Approved, cancellationToken);
        int fulfilled = await query.CountAsync(item => item.Status == RequestStatus.Fulfilled, cancellationToken);

        List<RequestReportRow> rows = await ProjectRows(query)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new RequestReportResult(rows, total, draft, submitted, approved, fulfilled, page, pageSize, totalPages);
    }

    public async Task<byte[]> ExportCsvAsync(RequestReportFilter filter, CancellationToken cancellationToken = default)
    {
        List<RequestReportRow> rows = await ProjectRows(BuildQuery(filter)).ToListAsync(cancellationToken);
        StringBuilder csv = new();
        csv.AppendLine("Reference,Organization,Country,Summary,Kind,Category,Priority,Status,Created UTC,Submitted UTC");
        foreach (RequestReportRow row in rows)
        {
            csv.AppendLine(string.Join(',', Escape(row.Reference), Escape(row.Organization), Escape(row.CountryCode), Escape(row.Summary), Escape(row.Kind), Escape(row.Category), Escape(row.Priority), Escape(row.Status), Escape(row.CreatedUtc.ToString("u")), Escape(row.SubmittedUtc?.ToString("u") ?? string.Empty)));
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    public async Task<byte[]> ExportXlsxAsync(RequestReportFilter filter, CancellationToken cancellationToken = default)
    {
        List<RequestReportRow> rows = await ProjectRows(BuildQuery(filter)).ToListAsync(cancellationToken);
        using MemoryStream stream = new();
        using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            WorkbookPart workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            SheetData sheetData = new();
            worksheetPart.Worksheet = new Worksheet(sheetData);
            Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Requests" });
            sheetData.Append(CreateRow("Reference", "Organization", "Country", "Summary", "Kind", "Category", "Priority", "Status", "Created UTC", "Submitted UTC"));
            foreach (RequestReportRow row in rows)
            {
                sheetData.Append(CreateRow(row.Reference, row.Organization, row.CountryCode, row.Summary, row.Kind, row.Category, row.Priority, row.Status, row.CreatedUtc.ToString("u"), row.SubmittedUtc?.ToString("u") ?? string.Empty));
            }
            workbookPart.Workbook.Save();
        }
        return stream.ToArray();
    }

    public async Task<byte[]> ExportPdfAsync(RequestReportFilter filter, CancellationToken cancellationToken = default)
    {
        List<RequestReportRow> rows = await ProjectRows(BuildQuery(filter)).ToListAsync(cancellationToken);
        return BuildPdf(rows);
    }

    public async Task<string> ExportPrintableHtmlAsync(RequestReportFilter filter, CancellationToken cancellationToken = default)
    {
        List<RequestReportRow> rows = await ProjectRows(BuildQuery(filter)).ToListAsync(cancellationToken);
        string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        StringBuilder html = new();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Request Portal Report</title>");
        html.Append("<style>@page{size:landscape;margin:12mm}body{font-family:Arial,sans-serif;color:#111;font-size:10pt}h1{margin:0 0 4px}p{margin:2px 0 12px;color:#555}table{border-collapse:collapse;width:100%}th,td{border:1px solid #bbb;padding:5px;vertical-align:top}th{background:#eee;text-align:left}tr{break-inside:avoid}.no-print{margin:0 0 12px}@media print{.no-print{display:none}}</style></head><body>");
        html.Append("<div class=\"no-print\"><button onclick=\"window.print()\">Print / Save as PDF</button></div>");
        html.Append("<h1>Request Portal Management Report</h1>");
        html.Append($"<p>Generated {DateTimeOffset.UtcNow:u} · {rows.Count} matching requests</p>");
        html.Append("<table><thead><tr><th>Reference</th><th>Organization</th><th>Country</th><th>Summary</th><th>Type</th><th>Category</th><th>Priority</th><th>Status</th><th>Created UTC</th><th>Submitted UTC</th></tr></thead><tbody>");
        foreach (RequestReportRow row in rows)
        {
            html.Append("<tr>");
            html.Append($"<td>{E(row.Reference)}</td><td>{E(row.Organization)}</td><td>{E(row.CountryCode)}</td><td>{E(row.Summary)}</td><td>{E(row.Kind)}</td><td>{E(row.Category)}</td><td>{E(row.Priority)}</td><td>{E(row.Status)}</td><td>{E(row.CreatedUtc.ToString("u"))}</td><td>{E(row.SubmittedUtc?.ToString("u"))}</td>");
            html.Append("</tr>");
        }
        html.Append("</tbody></table></body></html>");
        return html.ToString();
    }

    private IQueryable<AssistanceRequest> BuildQuery(RequestReportFilter filter)
    {
        IQueryable<AssistanceRequest> query = dbContext.AssistanceRequests.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            string term = filter.Search.Trim().ToLowerInvariant();
            query = query.Where(item => item.PublicReference.ToLower().Contains(term) || (item.Summary != null && item.Summary.ToLower().Contains(term)) || dbContext.Organizations.Any(organization => organization.Id == item.OrganizationId && organization.Name.ToLower().Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(filter.Status) && Enum.TryParse(filter.Status, true, out RequestStatus status)) query = query.Where(item => item.Status == status);
        if (!string.IsNullOrWhiteSpace(filter.Category)) query = query.Where(item => item.Category == filter.Category);
        if (!string.IsNullOrWhiteSpace(filter.Priority) && Enum.TryParse(filter.Priority, true, out RequestPriority priority)) query = query.Where(item => item.Priority == priority);
        if (filter.FromUtc.HasValue) query = query.Where(item => item.CreatedUtc >= filter.FromUtc.Value);
        if (filter.ToUtc.HasValue) query = query.Where(item => item.CreatedUtc <= filter.ToUtc.Value);
        if (!string.IsNullOrWhiteSpace(filter.CountryCode))
        {
            string countryCode = filter.CountryCode.Trim().ToUpperInvariant();
            query = query.Where(item => dbContext.Organizations.Any(organization => organization.Id == item.OrganizationId && organization.CountryCode == countryCode));
        }
        return query;
    }

    private IQueryable<RequestReportRow> ProjectRows(IQueryable<AssistanceRequest> query)
    {
        return from request in query
               join organization in dbContext.Organizations.AsNoTracking() on request.OrganizationId equals organization.Id
               orderby request.CreatedUtc descending
               select new RequestReportRow(request.Id, request.PublicReference, organization.Name, organization.CountryCode, request.Summary ?? string.Empty, request.Kind.ToString(), request.Category ?? string.Empty, request.Priority.ToString(), request.Status.ToString(), request.CreatedUtc, request.SubmittedUtc);
    }

    private static byte[] BuildPdf(IReadOnlyList<RequestReportRow> rows)
    {
        const int rowsPerPage = 36;
        RequestReportRow[][] pages = rows.Count == 0
            ? [Array.Empty<RequestReportRow>()]
            : rows.Chunk(rowsPerPage).Select(chunk => chunk.ToArray()).ToArray();

        using MemoryStream stream = new();
        using StreamWriter writer = new(stream, Encoding.ASCII, 1024, leaveOpen: true)
        {
            NewLine = "\n"
        };

        Dictionary<int, long> offsets = new();
        int catalogId = 1;
        int pagesId = 2;
        int fontId = 3;
        int maxId = 3 + (pages.Length * 2);

        writer.WriteLine("%PDF-1.4");
        writer.Flush();

        void WriteObject(int id, string body)
        {
            writer.Flush();
            offsets[id] = stream.Position;
            writer.WriteLine($"{id} 0 obj");
            writer.WriteLine(body);
            writer.WriteLine("endobj");
            writer.Flush();
        }

        string kids = string.Join(" ", Enumerable.Range(0, pages.Length).Select(index => $"{4 + (index * 2)} 0 R"));
        WriteObject(catalogId, $"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        WriteObject(pagesId, $"<< /Type /Pages /Count {pages.Length} /Kids [{kids}] >>");
        WriteObject(fontId, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
        {
            int pageId = 4 + (pageIndex * 2);
            int contentId = pageId + 1;
            string content = BuildPdfPageContent(pages[pageIndex], pageIndex + 1, pages.Length, rows.Count);
            WriteObject(pageId, $"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {contentId} 0 R >>");

            writer.Flush();
            offsets[contentId] = stream.Position;
            writer.WriteLine($"{contentId} 0 obj");
            writer.WriteLine($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>");
            writer.WriteLine("stream");
            writer.Flush();
            byte[] bytes = Encoding.ASCII.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            writer.WriteLine();
            writer.WriteLine("endstream");
            writer.WriteLine("endobj");
            writer.Flush();
        }

        long xrefOffset = stream.Position;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {maxId + 1}");
        writer.WriteLine("0000000000 65535 f ");
        for (int id = 1; id <= maxId; id++)
        {
            writer.WriteLine($"{offsets[id]:D10} 00000 n ");
        }
        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {maxId + 1} /Root {catalogId} 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(xrefOffset);
        writer.WriteLine("%%EOF");
        writer.Flush();
        return stream.ToArray();
    }

    private static string BuildPdfPageContent(
        IReadOnlyList<RequestReportRow> rows,
        int pageNumber,
        int pageCount,
        int totalRows)
    {
        StringBuilder content = new();
        content.AppendLine("BT");
        content.AppendLine("/F1 12 Tf");
        content.AppendLine("40 760 Td");
        content.AppendLine($"({PdfText("Request Portal Management Report")}) Tj");
        content.AppendLine("0 -16 Td");
        content.AppendLine("/F1 8 Tf");
        content.AppendLine($"({PdfText($"Generated {DateTimeOffset.UtcNow:u} | {totalRows} matching requests | Page {pageNumber} of {pageCount}")}) Tj");
        content.AppendLine("0 -20 Td");
        content.AppendLine("/F1 7 Tf");
        content.AppendLine($"({PdfText("Reference | Organization | Status | Category | Created UTC")}) Tj");

        foreach (RequestReportRow row in rows)
        {
            content.AppendLine("0 -15 Td");
            string line = $"{Clip(row.Reference, 18)} | {Clip(row.Organization, 28)} | {Clip(row.Status, 16)} | {Clip(row.Category, 20)} | {row.CreatedUtc:u}";
            content.AppendLine($"({PdfText(line)}) Tj");
        }

        content.AppendLine("ET");
        return content.ToString();
    }

    private static string Clip(string? value, int max)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? "-" : value.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= max ? normalized : normalized[..Math.Max(1, max - 3)] + "...";
    }

    private static string PdfText(string value)
    {
        StringBuilder output = new(value.Length);
        foreach (char ch in value)
        {
            char safe = ch is >= ' ' and <= '~' ? ch : '?';
            if (safe is '\\' or '(' or ')') output.Append('\\');
            output.Append(safe);
        }
        return output.ToString();
    }

    private static Row CreateRow(params string[] values)
    {
        Row row = new();
        foreach (string value in values) row.Append(new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new Text(value ?? string.Empty)) });
        return row;
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
