using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Integration;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Integration;

public sealed partial class IntegrationStatusService
{
    private IQueryable<MonitorRow> MonitorQuery()
    {
        var stale = clock.UtcNow.AddMinutes(-5);
        return from request in dbContext.AssistanceRequests.AsNoTracking()
               join link in dbContext.IntegrationLinks.AsNoTracking().Where(item => item.SystemName == ffpManagerOptions.SystemName)
                   on request.Id equals link.RequestId into links
               from link in links.DefaultIfEmpty()
               join intake in dbContext.QuickIntakes.AsNoTracking() on request.Id equals intake.RequestId into intakes
               from intake in intakes.DefaultIfEmpty()
               join user in dbContext.Users.AsNoTracking() on (request.SubmittedByUserId ?? request.CreatedByUserId) equals user.Id into users
               from user in users.DefaultIfEmpty()
               where request.Status != RequestStatus.Draft
               let queued = dbContext.OutboxMessages.Any(message => message.RequestId == request.Id &&
                   (message.MessageType.EndsWith("FfpManagerSubmissionRequestedMessage") || message.MessageType.EndsWith("RequestSubmittedMessage")))
               let missingQueue = !queued && (link == null || link.Status != SynchronizationStatus.Synchronized)
               let received = request.SubmittedUtc ?? request.CreatedUtc
               let stalePending = link != null && (link.Status == SynchronizationStatus.Pending || link.Status == SynchronizationStatus.NotSent) &&
                   (link.LastAttemptUtc ?? link.CreatedUtc) < stale
               let staleRetry = link != null && link.NextRetryUtc != null && link.NextRetryUtc < stale
               let unverified = link != null && link.Status == SynchronizationStatus.Synchronized && (link.VerifiedUtc == null || link.ArtifactsSynchronizedUtc == null)
               select new MonitorRow
               {
                   RequestId = request.Id, LinkId = link == null ? null : link.Id, PublicReference = request.PublicReference,
                   ApplicantName = intake != null ? (intake.FirstName + " " + intake.LastName).Trim()
                       : user == null ? "Name not provided" : ((user.FirstName ?? "") + " " + (user.LastName ?? "")).Trim() == ""
                           ? user.DisplayName : ((user.FirstName ?? "") + " " + (user.LastName ?? "")).Trim(),
                   Source = intake == null ? "Request Portal" : "Quick Intake", SubmittedUtc = received,
                   ApplicantTrn = user == null ? null : user.NationalIdTrn,
                   RecordedError = link == null || link.ErrorCode == null ? null : link.LastError,
                   Status = stalePending || staleRetry || unverified || (link == null && received < stale) || (missingQueue && received < stale)
                       ? SynchronizationStatus.ReconciliationRequired : link == null || link.Status == SynchronizationStatus.NotSent ? SynchronizationStatus.Pending
                           : link.FailureClassification == "Ambiguous" ? SynchronizationStatus.ReconciliationRequired
                           : link.NextRetryUtc != null ? SynchronizationStatus.Retrying : link.Status,
                   Code = link == null ? "MISSING_LINK" : missingQueue ? "MISSING_QUEUE" : stalePending ? "STALE_PENDING"
                       : staleRetry ? "STALE_RETRY" : unverified ? "UNVERIFIED_TARGET" : link.ErrorCode == null && link.Status == SynchronizationStatus.Failed ? "LEGACY_FAILURE" : link.ErrorCode,
                   Stage = link == null || missingQueue ? "Queue" : stalePending || staleRetry ? "Worker" : link.FailureStage,
                   Classification = link == null || missingQueue || stalePending || staleRetry || unverified ? "Reconciliation" : link.FailureClassification,
                   TargetServer = link == null ? null : link.TargetServer, TargetDatabase = link == null ? null : link.TargetDatabase,
                   // Legacy failures predate link-level counters; retain the existing submission outbox evidence.
                   AttemptCount = Math.Max(link == null ? 0 : link.AttemptCount,
                       dbContext.OutboxMessages.Where(message => message.RequestId == request.Id &&
                           message.MessageType.EndsWith("FfpManagerSubmissionRequestedMessage"))
                           .Select(message => (int?)message.AttemptCount).Max() ?? 0),
                   LastAttemptUtc = link == null ? null : link.LastAttemptUtc,
                   NextRetryUtc = link == null ? null : link.NextRetryUtc, SynchronizedUtc = link == null ? null : link.SynchronizedUtc,
                   VerifiedUtc = link == null ? null : link.VerifiedUtc, ApplicationId = link == null ? null : link.ExternalReference,
                   ApplicationNumber = link == null ? null : link.ApplicationNumber, HasQueuedSubmission = queued
               };
    }

    public async Task<IntegrationMonitorPage> SearchAsync(IntegrationMonitorQuery query, CancellationToken cancellationToken = default)
    {
        var rows = MonitorQuery();
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            foreach (string term in query.Search.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(10))
            {
                string pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                rows = rows.Where(item => EF.Functions.ILike(item.ApplicantName, pattern, "\\") ||
                    EF.Functions.ILike(item.PublicReference, pattern, "\\") ||
                    (item.ApplicationNumber != null && EF.Functions.ILike(item.ApplicationNumber, pattern, "\\")) ||
                    (item.ApplicationId != null && EF.Functions.ILike(item.ApplicationId, pattern, "\\")) ||
                    (item.ApplicantTrn != null && EF.Functions.ILike(item.ApplicantTrn, pattern, "\\")));
            }
        }
        if (query.FromUtc.HasValue) rows = rows.Where(item => item.SubmittedUtc >= query.FromUtc.Value);
        if (query.ThroughUtc.HasValue) rows = rows.Where(item => item.SubmittedUtc < query.ThroughUtc.Value);
        var counts = await rows.GroupBy(item => item.Status).Select(group => new { Status = group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        int Count(SynchronizationStatus status) => counts.FirstOrDefault(item => item.Status == status)?.Count ?? 0;
        var summary = new IntegrationMonitorCounts(Count(SynchronizationStatus.Failed), Count(SynchronizationStatus.Pending) + Count(SynchronizationStatus.NotSent),
            Count(SynchronizationStatus.Retrying), Count(SynchronizationStatus.Synchronized), Count(SynchronizationStatus.ReconciliationRequired));
        if (Enum.TryParse<SynchronizationStatus>(query.Status, true, out var status)) rows = rows.Where(item => item.Status == status);
        int total = await rows.CountAsync(cancellationToken);
        int size = query.PageSize is 25 or 50 or 100 ? query.PageSize : 25;
        int page = Math.Clamp(query.Page, 1, Math.Max(1, (int)Math.Ceiling(total / (double)size)));
        var sorted = query.Sort switch {
            "activity" => rows.OrderByDescending(item =>
                (item.VerifiedUtc ?? item.SubmittedUtc) > (item.LastAttemptUtc ?? item.SubmittedUtc)
                    ? ((item.VerifiedUtc ?? item.SubmittedUtc) > (item.SynchronizedUtc ?? item.SubmittedUtc) ? item.VerifiedUtc ?? item.SubmittedUtc : item.SynchronizedUtc ?? item.SubmittedUtc)
                    : ((item.LastAttemptUtc ?? item.SubmittedUtc) > (item.SynchronizedUtc ?? item.SubmittedUtc) ? item.LastAttemptUtc ?? item.SubmittedUtc : item.SynchronizedUtc ?? item.SubmittedUtc)),
            "oldest" => rows.OrderBy(item => item.SubmittedUtc),
            "applicant" => rows.OrderBy(item => item.ApplicantName),
            "status" => rows.OrderBy(item => item.Status),
            "attempt" => rows.OrderBy(item => item.LastAttemptUtc == null).ThenByDescending(item => item.LastAttemptUtc),
            _ => rows.OrderByDescending(item => item.SubmittedUtc)
        };
        var items = await sorted.ThenBy(item => item.RequestId).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        return new(items.Select(ToView).ToArray(), total, page, size, summary);
    }

    public async Task<int> CountAttentionAsync(CancellationToken cancellationToken = default)
        => await MonitorQuery().CountAsync(item => item.Status == SynchronizationStatus.Failed || item.Status == SynchronizationStatus.ReconciliationRequired, cancellationToken)
            + await GetReturnFailureCountAsync(cancellationToken);

    public async Task<IntegrationTransferDetail?> GetRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var row = await MonitorQuery().SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
        if (row is null) return null;
        var attempts = row.LinkId is null ? [] : await GetAttemptPageAsync(row.LinkId.Value, 1, cancellationToken);
        int attemptTotal = row.LinkId is null ? 0 : await dbContext.IntegrationAttempts.CountAsync(item => item.LinkId == row.LinkId, cancellationToken);
        var events = await dbContext.AuditEntries.AsNoTracking().Where(item =>
                (item.EntityType == "IntegrationLink" && item.EntityId == row.LinkId) ||
                (item.EntityType == "OutboxMessage" && dbContext.OutboxMessages.Any(message => message.RequestId == requestId && message.Id == item.EntityId)))
            .OrderByDescending(item => item.OccurredUtc).Take(100).Select(item => new IntegrationOperatorEvent(item.OccurredUtc, item.Action, item.Detail, item.ActorUserId)).ToListAsync(cancellationToken);
        return new(ToView(row), attempts, events, attemptTotal);
    }

    public async Task<IReadOnlyList<IntegrationAttemptView>> GetAttemptPageAsync(Guid linkId, int page, CancellationToken cancellationToken = default)
        => await dbContext.IntegrationAttempts.AsNoTracking().Where(item => item.LinkId == linkId)
            .OrderByDescending(item => item.StartedUtc).ThenBy(item => item.Id).Skip((Math.Max(1,page)-1)*100).Take(100)
            .Select(item => new IntegrationAttemptView(item.Id, item.Number, item.Operation, item.StartedUtc, item.CompletedUtc,
                item.Succeeded, item.Stage, item.Classification, item.ErrorCode, item.Explanation)).ToListAsync(cancellationToken);

    public async Task<IntegrationTransferRow?> GetTransferAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        var row = await MonitorQuery().SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
        return row is null ? null : ToView(row);
    }

    private IntegrationTransferRow ToView(MonitorRow row) => new(row.RequestId, row.LinkId, row.PublicReference, row.ApplicantName,
        row.Source, ffpManagerOptions.SystemName, row.TargetServer == null ? "JMDB01 / FFPJM (expected; unverified)" : $"{row.TargetServer} / {row.TargetDatabase}",
        row.Status, row.Stage, row.Classification, row.Code, row.RecordedError ?? (row.Code switch {
            "LEGACY_FAILURE" => "A historical failure was recorded before persistent attempt diagnostics were installed. Reconcile using protected production evidence before retrying.",
            "MISSING_LINK" => "No durable integration link exists. Reconcile the immutable request ID before recovery.",
            "MISSING_QUEUE" => "No submission queue record exists. Reconcile the destination before an authorized retry.",
            "STALE_PENDING" => "No transfer checkpoint within five minutes. Inspect the worker and destination before retrying.",
            "STALE_RETRY" => "The scheduled retry is more than five minutes overdue. Inspect worker health.",
            "UNVERIFIED_TARGET" => "Legacy synchronized status has not been verified against the immutable destination identity.",
            "DESTINATION_NOT_FOUND" => "No active destination record matches this immutable request ID. An authorized retry can submit it.",
            null => null,
            _ => TransferFailures.For(row.Stage ?? "Unknown", row.Code).Explanation
        }), row.SubmittedUtc, row.LastAttemptUtc, row.AttemptCount, row.NextRetryUtc, row.SynchronizedUtc,
        row.VerifiedUtc, row.ApplicationId, row.ApplicationNumber, row.HasQueuedSubmission) { RecordedError = row.RecordedError, RecoveryAction = RecoveryAction(row) };

    private static string? RecoveryAction(MonitorRow row)
    {
        if (row.Code is null) return null;
        // Use the existing diagnostic catalog only when it recognizes this stored code.
        // Unknown or historical failures must not acquire invented recovery instructions.
        var guidance = TransferFailures.For(row.Stage ?? "Unknown", row.Code);
        if (guidance.Code != row.Code || guidance.Code == "UNCLASSIFIED") return null;
        string text = guidance.Explanation;
        int sqlNumber = text.IndexOf(" SQL error number:", StringComparison.Ordinal);
        if (sqlNumber >= 0) text = text[..sqlNumber];
        int description = text.IndexOf(". ", StringComparison.Ordinal);
        return description >= 0 ? text[(description + 2)..] : text;
    }

    private sealed class MonitorRow
    {
        public Guid RequestId { get; set; }
        public Guid? LinkId { get; set; }
        public string PublicReference { get; set; } = "";
        public string ApplicantName { get; set; } = "";
        public string Source { get; set; } = "";
        public string? ApplicantTrn { get; set; }
        public string? RecordedError { get; set; }
        public SynchronizationStatus Status { get; set; }
        public string? Code { get; set; }
        public string? Stage { get; set; }
        public string? Classification { get; set; }
        public string? TargetServer { get; set; }
        public string? TargetDatabase { get; set; }
        public DateTimeOffset SubmittedUtc { get; set; }
        public int AttemptCount { get; set; }
        public DateTimeOffset? LastAttemptUtc { get; set; }
        public DateTimeOffset? NextRetryUtc { get; set; }
        public DateTimeOffset? SynchronizedUtc { get; set; }
        public DateTimeOffset? VerifiedUtc { get; set; }
        public string? ApplicationId { get; set; }
        public string? ApplicationNumber { get; set; }
        public bool HasQueuedSubmission { get; set; }
    }
}
