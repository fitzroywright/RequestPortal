using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Reporting;

public sealed record RequestReportFilter(
    string? Search,
    string? Status,
    string? CountryCode,
    string? Category,
    string? Priority,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    int Page = 1,
    int PageSize = 100);

public sealed record RequestReportRow(
    Guid Id,
    string Reference,
    string Organization,
    string CountryCode,
    string Summary,
    string Kind,
    string Category,
    string Priority,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? SubmittedUtc);

public sealed record RequestReportResult(
    IReadOnlyList<RequestReportRow> Rows,
    int Total,
    int Draft,
    int Submitted,
    int Approved,
    int Fulfilled,
    int Page = 1,
    int PageSize = 100,
    int TotalPages = 1);

public interface IStaffReportingService
{
    Task<RequestReportResult> SearchRequestsAsync(RequestReportFilter filter, CancellationToken cancellationToken = default);
    Task<byte[]> ExportCsvAsync(RequestReportFilter filter, CancellationToken cancellationToken = default);
    Task<byte[]> ExportXlsxAsync(RequestReportFilter filter, CancellationToken cancellationToken = default);
}
