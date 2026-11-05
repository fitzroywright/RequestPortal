using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Domain.Integration;

namespace RequestPortal.Application.Integration;

public sealed record IntegrationStatusSummary(
    Guid LinkId,
    Guid RequestId,
    string PublicReference,
    string OrganizationName,
    string SystemName,
    SynchronizationStatus Status,
    string? ExternalReference,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? SynchronizedUtc,
    string? LastError);

public interface IIntegrationStatusService
{
    Task<IntegrationMonitorPage> SearchAsync(IntegrationMonitorQuery query, CancellationToken cancellationToken = default);
    Task<IntegrationTransferDetail?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<IntegrationTransferRow?> GetTransferAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntegrationAttemptView>> GetAttemptPageAsync(Guid linkId, int page, CancellationToken cancellationToken = default);
    Task<int> CountAttentionAsync(CancellationToken cancellationToken = default);
    Task ObserveReturnOutboxAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntegrationReturnObservation>> GetReturnHistoryAsync(CancellationToken cancellationToken = default);
    Task ReconcileRequestAsync(Guid staffUserId, Guid requestId, CancellationToken cancellationToken = default);
    Task<Guid> EnsureLinkAsync(Guid requestId, string systemName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntegrationStatusSummary>> GetAllAsync(CancellationToken cancellationToken = default);
    Task MarkPendingAsync(Guid linkId, CancellationToken cancellationToken = default);
    Task MarkSynchronizedAsync(Guid linkId, string externalReference, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(Guid linkId, string error, CancellationToken cancellationToken = default);
    Task RecordFailureAsync(Guid linkId, TransferFailure failure, CancellationToken cancellationToken = default);
    Task RetryAsync(Guid staffUserId, Guid linkId, CancellationToken cancellationToken = default);
    Task ReconcileAsync(Guid staffUserId, Guid linkId, CancellationToken cancellationToken = default);
}

public sealed record IntegrationMonitorQuery(string? Search = null, string? Status = null,
    DateTimeOffset? FromUtc = null, DateTimeOffset? ThroughUtc = null, string Sort = "received", int Page = 1, int PageSize = 25);
public sealed record IntegrationTransferRow(Guid RequestId, Guid? LinkId, string PublicReference, string ApplicantName,
    string Source, string SystemName, string Target, SynchronizationStatus Status, string? FailureStage,
    string? FailureClassification, string? ErrorCode, string? Explanation, DateTimeOffset SubmittedUtc,
    DateTimeOffset? LastAttemptUtc, int AttemptCount, DateTimeOffset? NextRetryUtc, DateTimeOffset? SynchronizedUtc,
    DateTimeOffset? VerifiedUtc, string? ApplicationId, string? ApplicationNumber, bool HasQueuedSubmission)
{
    // Only classified, sanitized failures may be exposed; legacy provider payloads stay protected.
    public string? RecordedError { get; init; }
    public string? RecoveryAction { get; init; }
}
public sealed record IntegrationMonitorCounts(int Failed, int Pending, int Retrying, int Synchronized, int ReconciliationRequired)
{ public int NeedsAttention => Failed + ReconciliationRequired; }
public sealed record IntegrationMonitorPage(IReadOnlyList<IntegrationTransferRow> Items, int TotalCount, int Page, int PageSize,
    IntegrationMonitorCounts Counts)
{ public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize)); }
public sealed record IntegrationAttemptView(Guid Id, int Number, string Operation, DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc, bool Succeeded, string Stage, string? Classification, string? ErrorCode, string? Explanation);
public sealed record IntegrationOperatorEvent(DateTimeOffset OccurredUtc, string Action, string? Detail, Guid ActorUserId = default);
public sealed record IntegrationTransferDetail(IntegrationTransferRow Transfer, IReadOnlyList<IntegrationAttemptView> Attempts,
    IReadOnlyList<IntegrationOperatorEvent> OperatorEvents, int AttemptTotal = 0);
public sealed record IntegrationReturnObservation(DateTimeOffset ObservedUtc, ExternalReturnOutboxStatus? Snapshot, string? ErrorCode);
