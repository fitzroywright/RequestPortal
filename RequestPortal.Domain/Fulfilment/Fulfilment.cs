using System;

namespace RequestPortal.Domain.Fulfilment;

public enum FulfilmentSource
{
    LocalIcp = 0,
    FftpUs = 1
}

public enum FulfilmentStatus
{
    Pending = 0,
    InProgress = 1,
    Fulfilled = 2,
    Cancelled = 3
}

public sealed class Fulfilment
{
    private Fulfilment()
    {
    }

    public Fulfilment(Guid id, Guid requestId, Guid organizationId, FulfilmentSource source, DateTimeOffset createdUtc)
    {
        Id = id;
        RequestId = requestId;
        OrganizationId = organizationId;
        Source = source;
        Status = FulfilmentStatus.Pending;
        CreatedUtc = createdUtc;
    }

    public Guid Id { get; private set; }

    public Guid RequestId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public FulfilmentSource Source { get; private set; }

    public FulfilmentStatus Status { get; private set; }

    public string? ExternalReference { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset? FulfilledUtc { get; private set; }

    public string? Notes { get; private set; }

    public void MarkInProgress(string? externalReference)
    {
        if (Status != FulfilmentStatus.Pending)
        {
            throw new InvalidOperationException("Only pending fulfilments can start processing.");
        }

        Status = FulfilmentStatus.InProgress;
        ExternalReference = string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim();
    }

    public void Complete(DateTimeOffset fulfilledUtc, string? notes)
    {
        if (Status != FulfilmentStatus.Pending && Status != FulfilmentStatus.InProgress)
        {
            throw new InvalidOperationException("This fulfilment cannot be completed from its current status.");
        }

        Status = FulfilmentStatus.Fulfilled;
        FulfilledUtc = fulfilledUtc;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
    }
}
