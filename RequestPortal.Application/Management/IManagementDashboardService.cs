namespace RequestPortal.Application.Management;

public sealed record ManagementDashboardSnapshot(
    DateTimeOffset GeneratedUtc,
    ManagementExecutiveSummary Executive,
    IReadOnlyList<ManagementStatusMetric> Statuses,
    ManagementAttentionSummary Attention,
    IReadOnlyList<ManagementNamedCount> Categories,
    IReadOnlyList<ManagementNamedCount> Programs,
    ManagementDeliverySummary Delivery,
    ManagementDeliveryAnalytics DeliveryAnalytics,
    ManagementPerformanceMetrics Performance,
    IReadOnlyList<ManagementTrendPoint> Trend,
    IReadOnlyList<ParishReachSummary> Parishes);

public sealed record ManagementExecutiveSummary(
    int Today,
    int ThisWeek,
    int ThisMonth,
    int Outstanding,
    int Completed);

public sealed record ManagementStatusMetric(string Key, string Label, int Count);

public sealed record ManagementAttentionSummary(int Overdue, int Stalled, int MissingDocuments);

public sealed record ManagementNamedCount(string Name, int Count);

public sealed record ManagementDeliverySummary(
    int AwaitingAcknowledgement,
    int Delivered,
    int PartiallyDelivered,
    int Failed,
    int PendingFfpManagerSync);

public sealed record ManagementDeliveryDimension(
    string Name,
    int AwaitingAcknowledgement,
    int Delivered,
    int PartiallyDelivered,
    int Failed);

public sealed record ManagementDeliveryTrendPoint(
    DateOnly WeekStarting,
    int Delivered,
    int PartiallyDelivered,
    int Failed);

public sealed record ManagementDeliveryAnalytics(
    IReadOnlyList<ManagementDeliveryDimension> Organizations,
    IReadOnlyList<ManagementDeliveryDimension> Programs,
    IReadOnlyList<ManagementDeliveryDimension> Parishes,
    IReadOnlyList<ManagementDeliveryTrendPoint> Trend);

public sealed record ManagementPerformanceMetrics(
    double AverageProcessingDays,
    double MedianProcessingDays,
    double AverageOutstandingAgeDays,
    double CompletionRatePercent);

public sealed record ManagementTrendPoint(DateOnly WeekStarting, int Received, int Completed);

public sealed record ParishReachSummary(
    string Parish,
    int Requests,
    int Communities,
    int ExactLocations,
    int ApproximateLocations,
    double MapX,
    double MapY);

public sealed record CommunityReachSummary(string Community, int Requests);

public sealed record ManagementRequestRow(
    Guid Id,
    string PublicReference,
    Guid OrganizationId,
    string OrganizationName,
    string Status,
    string ManagementStatus,
    string? Category,
    string? Program,
    string? Location,
    string? Parish,
    string? Community,
    string? Precision,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? SubmittedUtc,
    DateTimeOffset LastActivityUtc,
    bool Overdue,
    bool Stalled,
    bool MissingDocuments);

public sealed record ManagementRequestFilter(
    string? Period = null,
    string? Status = null,
    string? Attention = null,
    string? Category = null,
    string? Program = null,
    string? Parish = null,
    string? Community = null,
    Guid? OrganizationId = null,
    Guid? RequestId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);

public sealed record ManagementDeliveryFilter(
    string? Status = null,
    string? Organization = null,
    string? Program = null,
    string? Parish = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);

public sealed record ManagementDeliveryRow(
    Guid DeliveryId,
    Guid RequestId,
    string RequestReference,
    string OrganizationName,
    string? Program,
    string? Parish,
    string DocumentReference,
    string DocumentType,
    string RecipientName,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? AcknowledgedUtc,
    string? AcknowledgedBy,
    string? Notes,
    Guid? EvidenceDocumentId);

public sealed record ParishDrillDown(
    string Parish,
    IReadOnlyList<CommunityReachSummary> Communities,
    IReadOnlyList<ManagementRequestRow> Requests);

public sealed record ManagementDashboardFilter(
    Guid? OrganizationId = null,
    Guid? RequestId = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null);

public interface IManagementDashboardService
{
    Task<ManagementDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default);
    Task<ManagementDashboardSnapshot> GetDashboardAsync(ManagementDashboardFilter filter, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ManagementRequestRow>> GetRequestsAsync(ManagementRequestFilter filter, CancellationToken cancellationToken = default);
    Task<ParishDrillDown> GetParishAsync(string parish, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ManagementDeliveryRow>> GetDeliveriesAsync(ManagementDeliveryFilter filter, CancellationToken cancellationToken = default);
}

public enum LocationPrecision
{
    Parish = 0,
    Community = 1,
    Address = 2,
    Exact = 3
}

public sealed record RequestLocationSnapshot(
    Guid RequestId,
    string? SourceText,
    string Parish,
    string? Community,
    decimal? Latitude,
    decimal? Longitude,
    LocationPrecision Precision,
    DateTimeOffset ResolvedUtc);

public interface IRequestLocationService
{
    Task<RequestLocationSnapshot?> ResolveAndSaveAsync(
        Guid requestId,
        string? locationText,
        double? latitude,
        double? longitude,
        CancellationToken cancellationToken = default);
}
