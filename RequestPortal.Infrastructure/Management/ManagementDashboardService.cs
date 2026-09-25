using Common.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Management;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Delivery;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Management;

public sealed class ManagementDashboardService : IManagementDashboardService
{
    private static readonly RequestStatus[] OutstandingStatuses =
    [
        RequestStatus.Submitted,
        RequestStatus.MoreInformationRequired,
        RequestStatus.Approved,
        RequestStatus.PendingFulfilment
    ];

    private static readonly RequestStatus[] CompletedStatuses =
    [
        RequestStatus.Fulfilled,
        RequestStatus.Closed
    ];

    private static readonly RequestStatus[] ProcessingTerminalStatuses =
    [
        RequestStatus.Rejected,
        RequestStatus.Fulfilled,
        RequestStatus.Closed
    ];

    private readonly RequestPortalDbContext dbContext;
    private readonly IFileStorage storage;
    private readonly ManagementDashboardOptions options;

    public ManagementDashboardService(
        RequestPortalDbContext dbContext,
        IFileStorage storage,
        IOptions<ManagementDashboardOptions> options)
    {
        this.dbContext = dbContext;
        this.storage = storage;
        this.options = options.Value;
    }

    public async Task<ManagementDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<ManagementRequestRow> rows = await LoadRowsAsync(now, cancellationToken);
        List<LifecycleTiming> timings = await LoadLifecycleTimingsAsync(cancellationToken);

        DateTimeOffset today = new(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        int daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
        DateTimeOffset weekStart = today.AddDays(-daysSinceMonday);
        DateTimeOffset monthStart = new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        ManagementExecutiveSummary executive = new(
            rows.Count(row => row.SubmittedUtc >= today),
            rows.Count(row => row.SubmittedUtc >= weekStart),
            rows.Count(row => row.SubmittedUtc >= monthStart),
            rows.Count(row => OutstandingStatuses.Contains(ParseStatus(row.Status))),
            rows.Count(row => CompletedStatuses.Contains(ParseStatus(row.Status))));

        IReadOnlyList<ManagementStatusMetric> statuses = BuildStatuses(rows, now);
        ManagementAttentionSummary attention = new(
            rows.Count(row => row.Overdue),
            rows.Count(row => row.Stalled),
            rows.Count(row => row.MissingDocuments));

        IReadOnlyList<ManagementNamedCount> categories = GroupNamed(rows, row => row.Category);
        IReadOnlyList<ManagementNamedCount> programs = GroupNamed(rows, row => row.Program);
        ManagementDeliverySummary delivery = await BuildDeliverySummaryAsync(cancellationToken);
        ManagementDeliveryAnalytics deliveryAnalytics = await BuildDeliveryAnalyticsAsync(rows, now, cancellationToken);
        ManagementPerformanceMetrics performance = BuildPerformance(rows, timings, now);
        IReadOnlyList<ManagementTrendPoint> trend = BuildTrend(rows, timings, now);
        IReadOnlyList<ParishReachSummary> parishes = BuildParishes(rows);

        return new ManagementDashboardSnapshot(
            now,
            executive,
            statuses,
            attention,
            categories,
            programs,
            delivery,
            deliveryAnalytics,
            performance,
            trend,
            parishes);
    }

    private async Task<ManagementDeliverySummary> BuildDeliverySummaryAsync(CancellationToken cancellationToken)
    {
        int awaiting = await dbContext.Deliveries.AsNoTracking().CountAsync(item => item.Status == DeliveryStatus.Ready, cancellationToken);
        int delivered = await dbContext.Deliveries.AsNoTracking().CountAsync(item => item.Status == DeliveryStatus.Delivered, cancellationToken);
        int partial = await dbContext.Deliveries.AsNoTracking().CountAsync(item => item.Status == DeliveryStatus.PartiallyDelivered, cancellationToken);
        int failed = await dbContext.Deliveries.AsNoTracking().CountAsync(item => item.Status == DeliveryStatus.Failed, cancellationToken);
        int pendingSync = await dbContext.Deliveries.AsNoTracking().CountAsync(
            item => item.Status != DeliveryStatus.Ready && item.SyncedToFfpManagerUtc == null,
            cancellationToken);
        return new ManagementDeliverySummary(awaiting, delivered, partial, failed, pendingSync);
    }

    private async Task<ManagementDeliveryAnalytics> BuildDeliveryAnalyticsAsync(
        IReadOnlyList<ManagementRequestRow> requestRows,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var deliveries = await dbContext.Deliveries
            .AsNoTracking()
            .Select(item => new { item.RequestId, item.CreatedUtc, item.AcknowledgedUtc })
            .ToListAsync(cancellationToken);

        Dictionary<Guid, ManagementRequestRow> rows = requestRows.ToDictionary(item => item.Id);
        var linked = deliveries
            .Where(item => rows.ContainsKey(item.RequestId))
            .Select(item => new { Delivery = item, Request = rows[item.RequestId] })
            .ToArray();

        IReadOnlyList<ManagementNamedCount> organizations = linked
            .GroupBy(item => item.Request.OrganizationName, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ManagementNamedCount(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name)
            .Take(12)
            .ToArray();

        IReadOnlyList<ManagementNamedCount> programs = linked
            .Where(item => !string.IsNullOrWhiteSpace(item.Request.Program))
            .GroupBy(item => item.Request.Program!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ManagementNamedCount(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name)
            .Take(12)
            .ToArray();

        IReadOnlyList<ManagementNamedCount> parishes = linked
            .Where(item => !string.IsNullOrWhiteSpace(item.Request.Parish))
            .GroupBy(item => item.Request.Parish!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ManagementNamedCount(group.Key, group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name)
            .Take(14)
            .ToArray();

        DateTimeOffset today = new(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        DateTimeOffset currentWeek = today.AddDays(-daysSinceMonday);
        DateTimeOffset firstWeek = currentWeek.AddDays(-49);

        IReadOnlyList<ManagementDeliveryTrendPoint> trend = Enumerable.Range(0, 8)
            .Select(index =>
            {
                DateTimeOffset start = firstWeek.AddDays(index * 7);
                DateTimeOffset end = start.AddDays(7);
                return new ManagementDeliveryTrendPoint(
                    DateOnly.FromDateTime(start.UtcDateTime),
                    deliveries.Count(item => item.CreatedUtc >= start && item.CreatedUtc < end),
                    deliveries.Count(item => item.AcknowledgedUtc >= start && item.AcknowledgedUtc < end));
            })
            .ToArray();

        return new ManagementDeliveryAnalytics(organizations, programs, parishes, trend);
    }

    public async Task<IReadOnlyList<ManagementRequestRow>> GetRequestsAsync(
        ManagementRequestFilter filter,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IEnumerable<ManagementRequestRow> rows = await LoadRowsAsync(now, cancellationToken);

        rows = ApplyPeriod(rows, filter.Period, now);
        rows = ApplyStatus(rows, filter.Status, now);
        rows = ApplyAttention(rows, filter.Attention);

        if (!string.IsNullOrWhiteSpace(filter.Category))
            rows = rows.Where(row => string.Equals(row.Category, filter.Category, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Program))
            rows = rows.Where(row => string.Equals(row.Program, filter.Program, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Parish))
            rows = rows.Where(row => string.Equals(row.Parish, filter.Parish, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Community))
            rows = rows.Where(row => string.Equals(row.Community, filter.Community, StringComparison.OrdinalIgnoreCase));

        return rows
            .OrderByDescending(row => row.SubmittedUtc ?? row.CreatedUtc)
            .ThenBy(row => row.PublicReference)
            .ToArray();
    }

    public async Task<ParishDrillDown> GetParishAsync(string parish, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ManagementRequestRow> requests = await GetRequestsAsync(
            new ManagementRequestFilter(Parish: parish),
            cancellationToken);
        IReadOnlyList<CommunityReachSummary> communities = requests
            .Where(row => !string.IsNullOrWhiteSpace(row.Community))
            .GroupBy(row => row.Community!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CommunityReachSummary(group.Key, group.Count()))
            .OrderByDescending(item => item.Requests)
            .ThenBy(item => item.Community)
            .ToArray();

        return new ParishDrillDown(parish, communities, requests);
    }

    private async Task<List<ManagementRequestRow>> LoadRowsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var baseRows = await (
            from request in dbContext.AssistanceRequests.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking() on request.OrganizationId equals organization.Id
            where request.Status != RequestStatus.Draft && request.Status != RequestStatus.Cancelled && request.Status != RequestStatus.Duplicate
            select new
            {
                request.Id,
                request.PublicReference,
                OrganizationName = organization.Name,
                request.Status,
                request.Category,
                Program = request.ProgramCategory,
                request.Location,
                request.CreatedUtc,
                request.SubmittedUtc
            }).ToListAsync(cancellationToken);

        Guid[] requestIds = baseRows.Select(row => row.Id).ToArray();
        Dictionary<Guid, DateTimeOffset> lastActivity = await dbContext.RequestStatusHistory
            .AsNoTracking()
            .Where(item => requestIds.Contains(item.RequestId))
            .GroupBy(item => item.RequestId)
            .Select(group => new { RequestId = group.Key, ChangedUtc = group.Max(item => item.ChangedUtc) })
            .ToDictionaryAsync(item => item.RequestId, item => item.ChangedUtc, cancellationToken);

        HashSet<Guid> requestIdsWithDocuments = (await dbContext.Documents
            .AsNoTracking()
            .Where(document =>
                requestIds.Contains(document.OwnerId) &&
                document.OwnerType == DocumentOwnerType.AssistanceRequest &&
                !document.IsArchived)
            .Select(document => document.OwnerId)
            .Distinct()
            .ToListAsync(cancellationToken))
            .ToHashSet();

        Task<(Guid RequestId, RequestLocationSnapshot? Location)>[] locationTasks = baseRows
            .Select(async row =>
            {
                RequestLocationSnapshot? location = await ReadLocationSafeAsync(row.Id, cancellationToken)
                    ?? RequestLocationService.ResolveExisting(row.Id, row.Location);
                return (row.Id, location);
            })
            .ToArray();
        (Guid RequestId, RequestLocationSnapshot? Location)[] locations = await Task.WhenAll(locationTasks);
        Dictionary<Guid, RequestLocationSnapshot> geo = locations
            .Where(item => item.Location is not null)
            .ToDictionary(item => item.RequestId, item => item.Location!);

        DateTimeOffset overdueCutoff = now.AddDays(-Math.Max(1, options.OverdueDays));
        DateTimeOffset stalledCutoff = now.AddDays(-Math.Max(1, options.StalledDays));

        return baseRows.Select(row =>
        {
            DateTimeOffset activity = lastActivity.GetValueOrDefault(row.Id, row.SubmittedUtc ?? row.CreatedUtc);
            bool outstanding = OutstandingStatuses.Contains(row.Status);
            bool overdue = outstanding && row.SubmittedUtc.HasValue && row.SubmittedUtc.Value <= overdueCutoff;
            bool stalled = outstanding && activity <= stalledCutoff;
            bool missingDocuments = (row.Status == RequestStatus.Submitted || row.Status == RequestStatus.MoreInformationRequired)
                && !requestIdsWithDocuments.Contains(row.Id);
            geo.TryGetValue(row.Id, out RequestLocationSnapshot? location);

            return new ManagementRequestRow(
                row.Id,
                row.PublicReference,
                row.OrganizationName,
                row.Status.ToString(),
                ToManagementStatus(row.Status, row.SubmittedUtc, now),
                row.Category,
                row.Program,
                row.Location,
                location?.Parish,
                location?.Community,
                location?.Precision.ToString(),
                row.CreatedUtc,
                row.SubmittedUtc,
                activity,
                overdue,
                stalled,
                missingDocuments);
        }).ToList();
    }

    private async Task<RequestLocationSnapshot?> ReadLocationSafeAsync(Guid requestId, CancellationToken cancellationToken)
    {
        try
        {
            return await RequestLocationStorage.ReadAsync(storage, requestId, cancellationToken);
        }
        catch (StorageException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<List<LifecycleTiming>> LoadLifecycleTimingsAsync(CancellationToken cancellationToken)
    {
        var history = await dbContext.RequestStatusHistory
            .AsNoTracking()
            .Where(item => item.Status == RequestStatus.Submitted || ProcessingTerminalStatuses.Contains(item.Status) || CompletedStatuses.Contains(item.Status))
            .OrderBy(item => item.ChangedUtc)
            .Select(item => new { item.RequestId, item.Status, item.ChangedUtc })
            .ToListAsync(cancellationToken);

        return history
            .GroupBy(item => item.RequestId)
            .Select(group =>
            {
                DateTimeOffset? submitted = group.Where(item => item.Status == RequestStatus.Submitted).Select(item => (DateTimeOffset?)item.ChangedUtc).FirstOrDefault();
                DateTimeOffset? terminal = group.Where(item => ProcessingTerminalStatuses.Contains(item.Status)).Select(item => (DateTimeOffset?)item.ChangedUtc).FirstOrDefault();
                DateTimeOffset? completed = group.Where(item => CompletedStatuses.Contains(item.Status)).Select(item => (DateTimeOffset?)item.ChangedUtc).FirstOrDefault();
                return new LifecycleTiming(group.Key, submitted, terminal, completed);
            })
            .ToList();
    }

    private IReadOnlyList<ManagementStatusMetric> BuildStatuses(IReadOnlyList<ManagementRequestRow> rows, DateTimeOffset now) =>
    [
        new("new", "New", rows.Count(row => row.ManagementStatus == "New")),
        new("under-review", "Under review", rows.Count(row => row.ManagementStatus == "Under review")),
        new("awaiting-info", "Awaiting info", rows.Count(row => row.ManagementStatus == "Awaiting info")),
        new("approved", "Approved", rows.Count(row => row.ManagementStatus == "Approved")),
        new("declined", "Declined", rows.Count(row => row.ManagementStatus == "Declined")),
        new("completed", "Completed", rows.Count(row => row.ManagementStatus == "Completed"))
    ];

    private ManagementPerformanceMetrics BuildPerformance(
        IReadOnlyList<ManagementRequestRow> rows,
        IReadOnlyList<LifecycleTiming> timings,
        DateTimeOffset now)
    {
        double[] processingDays = timings
            .Where(item => item.SubmittedUtc.HasValue && item.TerminalUtc.HasValue && item.TerminalUtc >= item.SubmittedUtc)
            .Select(item => (item.TerminalUtc!.Value - item.SubmittedUtc!.Value).TotalDays)
            .OrderBy(value => value)
            .ToArray();
        double averageProcessing = processingDays.Length == 0 ? 0 : processingDays.Average();
        double medianProcessing = Median(processingDays);

        double[] outstandingAges = rows
            .Where(row => OutstandingStatuses.Contains(ParseStatus(row.Status)) && row.SubmittedUtc.HasValue)
            .Select(row => Math.Max(0, (now - row.SubmittedUtc!.Value).TotalDays))
            .ToArray();
        double averageOutstanding = outstandingAges.Length == 0 ? 0 : outstandingAges.Average();

        int submitted = rows.Count(row => row.SubmittedUtc.HasValue);
        int completed = rows.Count(row => CompletedStatuses.Contains(ParseStatus(row.Status)));
        double completionRate = submitted == 0 ? 0 : completed * 100d / submitted;

        return new ManagementPerformanceMetrics(
            Math.Round(averageProcessing, 1),
            Math.Round(medianProcessing, 1),
            Math.Round(averageOutstanding, 1),
            Math.Round(completionRate, 1));
    }

    private IReadOnlyList<ManagementTrendPoint> BuildTrend(
        IReadOnlyList<ManagementRequestRow> rows,
        IReadOnlyList<LifecycleTiming> timings,
        DateTimeOffset now)
    {
        int weeks = Math.Clamp(options.TrendWeeks, 4, 52);
        DateTimeOffset today = new(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        DateTimeOffset currentWeek = today.AddDays(-daysSinceMonday);
        DateTimeOffset firstWeek = currentWeek.AddDays(-7 * (weeks - 1));

        return Enumerable.Range(0, weeks)
            .Select(index =>
            {
                DateTimeOffset start = firstWeek.AddDays(index * 7);
                DateTimeOffset end = start.AddDays(7);
                int received = rows.Count(row => row.SubmittedUtc >= start && row.SubmittedUtc < end);
                int completed = timings.Count(item => item.CompletedUtc >= start && item.CompletedUtc < end);
                return new ManagementTrendPoint(DateOnly.FromDateTime(start.UtcDateTime), received, completed);
            })
            .ToArray();
    }

    private static IReadOnlyList<ParishReachSummary> BuildParishes(IReadOnlyList<ManagementRequestRow> rows)
    {
        Dictionary<string, (double X, double Y)> points = ParishMapPoints();
        return rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Parish))
            .GroupBy(row => row.Parish!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                (double x, double y) = points.GetValueOrDefault(group.Key, (50, 50));
                return new ParishReachSummary(
                    group.Key,
                    group.Count(),
                    group.Where(row => !string.IsNullOrWhiteSpace(row.Community)).Select(row => row.Community!).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    group.Count(row => string.Equals(row.Precision, "Exact", StringComparison.OrdinalIgnoreCase)),
                    group.Count(row => !string.Equals(row.Precision, "Exact", StringComparison.OrdinalIgnoreCase)),
                    x,
                    y);
            })
            .OrderByDescending(item => item.Requests)
            .ThenBy(item => item.Parish)
            .ToArray();
    }

    private static IReadOnlyList<ManagementNamedCount> GroupNamed(
        IEnumerable<ManagementRequestRow> rows,
        Func<ManagementRequestRow, string?> selector) => rows
        .Select(selector)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .GroupBy(value => value!, StringComparer.OrdinalIgnoreCase)
        .Select(group => new ManagementNamedCount(group.Key, group.Count()))
        .OrderByDescending(item => item.Count)
        .ThenBy(item => item.Name)
        .Take(12)
        .ToArray();

    private string ToManagementStatus(RequestStatus status, DateTimeOffset? submittedUtc, DateTimeOffset now)
    {
        return status switch
        {
            RequestStatus.Submitted when submittedUtc >= now.AddHours(-Math.Max(1, options.NewRequestHours)) => "New",
            RequestStatus.Submitted => "Under review",
            RequestStatus.MoreInformationRequired => "Awaiting info",
            RequestStatus.Approved or RequestStatus.PendingFulfilment => "Approved",
            RequestStatus.Rejected => "Declined",
            RequestStatus.Fulfilled or RequestStatus.Closed => "Completed",
            _ => status.ToString()
        };
    }

    private static IEnumerable<ManagementRequestRow> ApplyPeriod(IEnumerable<ManagementRequestRow> rows, string? period, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(period)) return rows;
        DateTimeOffset today = new(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
        return period.ToLowerInvariant() switch
        {
            "today" => rows.Where(row => row.SubmittedUtc >= today),
            "week" => rows.Where(row => row.SubmittedUtc >= today.AddDays(-(((int)now.DayOfWeek + 6) % 7))),
            "month" => rows.Where(row => row.SubmittedUtc >= new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)),
            "outstanding" => rows.Where(row => OutstandingStatuses.Contains(ParseStatus(row.Status))),
            "completed" => rows.Where(row => CompletedStatuses.Contains(ParseStatus(row.Status))),
            _ => rows
        };
    }

    private static IEnumerable<ManagementRequestRow> ApplyStatus(IEnumerable<ManagementRequestRow> rows, string? status, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(status)) return rows;
        string normalized = status.Trim().Replace('-', ' ').ToLowerInvariant();
        return rows.Where(row => row.ManagementStatus.Equals(normalized switch
        {
            "new" => "New",
            "under review" => "Under review",
            "awaiting info" => "Awaiting info",
            "approved" => "Approved",
            "declined" => "Declined",
            "completed" => "Completed",
            _ => normalized
        }, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<ManagementRequestRow> ApplyAttention(IEnumerable<ManagementRequestRow> rows, string? attention) =>
        attention?.Trim().ToLowerInvariant() switch
        {
            "overdue" => rows.Where(row => row.Overdue),
            "stalled" => rows.Where(row => row.Stalled),
            "missing-docs" or "missing documents" => rows.Where(row => row.MissingDocuments),
            _ => rows
        };

    private static RequestStatus ParseStatus(string value) => Enum.TryParse(value, true, out RequestStatus parsed) ? parsed : RequestStatus.Submitted;

    private static double Median(double[] sorted)
    {
        if (sorted.Length == 0) return 0;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    private static Dictionary<string, (double X, double Y)> ParishMapPoints() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hanover"] = (9, 35), ["Westmoreland"] = (14, 61), ["St. James"] = (20, 28), ["Trelawny"] = (32, 27),
        ["St. Elizabeth"] = (31, 68), ["Manchester"] = (47, 61), ["St. Ann"] = (49, 30), ["Clarendon"] = (58, 65),
        ["St. Catherine"] = (68, 52), ["St. Mary"] = (66, 28), ["St. Andrew"] = (77, 55), ["Kingston"] = (81, 69),
        ["Portland"] = (86, 30), ["St. Thomas"] = (91, 59)
    };

    private sealed record LifecycleTiming(Guid RequestId, DateTimeOffset? SubmittedUtc, DateTimeOffset? TerminalUtc, DateTimeOffset? CompletedUtc);
}
