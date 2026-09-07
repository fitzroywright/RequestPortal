using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Reporting;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Reporting;

public sealed class StaffReportingService : IStaffReportingService
{
    private readonly RequestPortalDbContext dbContext;
    public StaffReportingService(RequestPortalDbContext dbContext) { this.dbContext = dbContext; }

    public async Task<RequestReportResult> SearchRequestsAsync(RequestReportFilter filter, CancellationToken cancellationToken = default)
    {
        IQueryable<AssistanceRequest> query = dbContext.AssistanceRequests.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Search)) { string term = filter.Search.Trim().ToLower(); query = query.Where(item => item.PublicReference.ToLower().Contains(term) || item.Summary.ToLower().Contains(term)); }
        if (!string.IsNullOrWhiteSpace(filter.Status) && Enum.TryParse(filter.Status, true, out RequestStatus status)) query = query.Where(item => item.Status == status);
        if (!string.IsNullOrWhiteSpace(filter.Category)) query = query.Where(item => item.Category == filter.Category);
        if (!string.IsNullOrWhiteSpace(filter.Priority) && Enum.TryParse(filter.Priority, true, out RequestPriority priority)) query = query.Where(item => item.Priority == priority);
        if (filter.FromUtc.HasValue) query = query.Where(item => item.CreatedUtc >= filter.FromUtc.Value);
        if (filter.ToUtc.HasValue) query = query.Where(item => item.CreatedUtc <= filter.ToUtc.Value);
        if (!string.IsNullOrWhiteSpace(filter.CountryCode))
        {
            Guid[] organizationIds = await dbContext.Organizations.AsNoTracking().Where(item => item.CountryCode == filter.CountryCode).Select(item => item.Id).ToArrayAsync(cancellationToken);
            query = query.Where(item => organizationIds.Contains(item.OrganizationId));
        }

        List<RequestReportRow> rows = await (from request in query join organization in dbContext.Organizations.AsNoTracking() on request.OrganizationId equals organization.Id orderby request.CreatedUtc descending select new RequestReportRow(request.Id, request.PublicReference, organization.Name, organization.CountryCode, request.Summary, request.Kind.ToString(), request.Category ?? string.Empty, request.Priority.ToString(), request.Status.ToString(), request.CreatedUtc, request.SubmittedUtc)).Take(1000).ToListAsync(cancellationToken);
        return new RequestReportResult(rows, rows.Count, rows.Count(row => row.Status == "Draft"), rows.Count(row => row.Status == "Submitted"), rows.Count(row => row.Status == "Approved"), rows.Count(row => row.Status == "Fulfilled"));
    }

    public async Task<byte[]> ExportCsvAsync(RequestReportFilter filter, CancellationToken cancellationToken = default)
    {
        RequestReportResult report = await SearchRequestsAsync(filter, cancellationToken);
        StringBuilder csv = new();
        csv.AppendLine("Reference,Organization,Country,Summary,Kind,Category,Priority,Status,Created UTC,Submitted UTC");
        foreach (RequestReportRow row in report.Rows) csv.AppendLine(string.Join(',', Escape(row.Reference), Escape(row.Organization), Escape(row.CountryCode), Escape(row.Summary), Escape(row.Kind), Escape(row.Category), Escape(row.Priority), Escape(row.Status), Escape(row.CreatedUtc.ToString("u")), Escape(row.SubmittedUtc?.ToString("u") ?? string.Empty)));
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static string Escape(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
