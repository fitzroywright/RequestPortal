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
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Fulfilment id is required.", nameof(id));
        }

        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("Request id is required.", nameof(requestId));
        }

        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("Organization id is required.", nameof(organizationId));
        }

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
        string? normalizedReference = Normalize(externalReference);

        if (Status == FulfilmentStatus.InProgress &&
            string.Equals(ExternalReference, normalizedReference, StringComparison.Ordinal))
        {
            return;
        }

        if (Status != FulfilmentStatus.Pending)
        {
            throw new InvalidOperationException("Only pending fulfilments can start processing.");
        }

        Status = FulfilmentStatus.InProgress;
        ExternalReference = normalizedReference;
    }

    public void Complete(DateTimeOffset fulfilledUtc, string? notes)
    {
        if (Status == FulfilmentStatus.Fulfilled)
        {
            return;
        }

        if (Status != FulfilmentStatus.Pending && Status != FulfilmentStatus.InProgress)
        {
            throw new InvalidOperationException("This fulfilment cannot be completed from its current status.");
        }

        Status = FulfilmentStatus.Fulfilled;
        FulfilledUtc = fulfilledUtc;
        Notes = Normalize(notes);
    }

    public void Cancel(string reason)
    {
        if (Status == FulfilmentStatus.Cancelled)
        {
            return;
        }

        if (Status == FulfilmentStatus.Fulfilled)
        {
            throw new InvalidOperationException("A fulfilled fulfilment cannot be cancelled.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A cancellation reason is required.", nameof(reason));
        }

        Status = FulfilmentStatus.Cancelled;
        Notes = reason.Trim();
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
