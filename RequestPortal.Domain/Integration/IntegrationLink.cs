using System;

namespace RequestPortal.Domain.Integration;

public enum SynchronizationStatus
{
    NotSent = 0,
    Pending = 1,
    Synchronized = 2,
    Failed = 3,
    Retrying = 4,
    ReconciliationRequired = 5
}

public sealed class IntegrationLink
{
    private IntegrationLink()
    {
    }

    public IntegrationLink(Guid id, Guid requestId, string systemName, DateTimeOffset createdUtc)
    {
        Id = id;
        RequestId = requestId;
        SystemName = systemName.Trim();
        Status = SynchronizationStatus.NotSent;
        CreatedUtc = createdUtc;
    }

    public Guid Id { get; private set; }

    public Guid RequestId { get; private set; }

    public string SystemName { get; private set; } = string.Empty;

    public string? ApplicationNumber { get; private set; }

    public string? ExternalReference { get; private set; }

    public SynchronizationStatus Status { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset? LastAttemptUtc { get; private set; }

    public DateTimeOffset? SynchronizedUtc { get; private set; }

    public string? LastError { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? NextRetryUtc { get; private set; }
    public DateTimeOffset? VerifiedUtc { get; private set; }
    public DateTimeOffset? ArtifactsSynchronizedUtc { get; private set; }
    public string? FailureStage { get; private set; }
    public string? FailureClassification { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? TargetServer { get; private set; }
    public string? TargetDatabase { get; private set; }
    public void MarkQueued() => Status = SynchronizationStatus.Pending;

    public void BeginAttempt(DateTimeOffset now)
    {
        AttemptCount++;
        MarkPending(now);
        NextRetryUtc = null;
    }

    public void RecordVerifiedDestination(string externalReference, string applicationNumber, string server, string database, DateTimeOffset now)
    {
        ExternalReference = externalReference;
        ApplicationNumber = applicationNumber;
        TargetServer = server;
        TargetDatabase = database;
        VerifiedUtc = now;
    }

    public void RecordFailure(DateTimeOffset now, string stage, string classification, string code, string explanation, bool transient)
    {
        MarkFailed(now, explanation);
        FailureStage = stage;
        FailureClassification = classification;
        ErrorCode = code;
        NextRetryUtc = transient && AttemptCount < 10 ? now.AddMinutes(Math.Pow(2, Math.Min(Math.Max(AttemptCount - 1, 0), 6))) : null;
        // Keep persisted status values compatible with the previous release. Monitoring derives
        // Retrying/Reconciliation Required from the durable schedule and classification.
        Status = SynchronizationStatus.Failed;
    }

    public void RequireReconciliation(DateTimeOffset now, string code, string explanation)
        => RecordFailure(now, "Reconciliation", "Ambiguous", code, explanation, false);

    public void MarkPending(DateTimeOffset attemptedUtc)
    {
        Status = SynchronizationStatus.Pending;
        LastAttemptUtc = attemptedUtc;
        LastError = null;
    }

    public void MarkSynchronized(string externalReference, DateTimeOffset synchronizedUtc, string? applicationNumber = null)
    {
        ExternalReference = externalReference.Trim();
        if (!string.IsNullOrWhiteSpace(applicationNumber))
            ApplicationNumber = applicationNumber.Trim();
        Status = SynchronizationStatus.Synchronized;
        LastAttemptUtc ??= synchronizedUtc;
        SynchronizedUtc ??= synchronizedUtc;
        ArtifactsSynchronizedUtc ??= synchronizedUtc;
        NextRetryUtc = null;
        FailureStage = null;
        FailureClassification = null;
        ErrorCode = null;
        LastError = null;
    }

    public void MarkFailed(DateTimeOffset attemptedUtc, string error)
    {
        Status = SynchronizationStatus.Failed;
        LastAttemptUtc = attemptedUtc;
        LastError = error;
    }
}
