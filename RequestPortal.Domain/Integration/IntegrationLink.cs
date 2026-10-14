using System;

namespace RequestPortal.Domain.Integration;

public enum SynchronizationStatus
{
    NotSent = 0,
    Pending = 1,
    Synchronized = 2,
    Failed = 3
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

    public void MarkPending(DateTimeOffset attemptedUtc)
    {
        Status = SynchronizationStatus.Pending;
        LastAttemptUtc = attemptedUtc;
        LastError = null;
    }

    public void MarkSynchronized(string externalReference, DateTimeOffset synchronizedUtc, string? applicationNumber = null)
    {
        ExternalReference = externalReference.Trim();
        ApplicationNumber = applicationNumber?.Trim();
        Status = SynchronizationStatus.Synchronized;
        LastAttemptUtc = synchronizedUtc;
        SynchronizedUtc = synchronizedUtc;
        LastError = null;
    }

    public void MarkFailed(DateTimeOffset attemptedUtc, string error)
    {
        Status = SynchronizationStatus.Failed;
        LastAttemptUtc = attemptedUtc;
        LastError = error;
    }
}
