using System;

namespace RequestPortal.Domain.Fulfilment;

public sealed class FulfilmentLine
{
    private FulfilmentLine()
    {
    }

    public FulfilmentLine(Guid id, Guid fulfilmentId, Guid requestItemId, decimal requestedQuantity)
    {
        if (id == Guid.Empty) throw new ArgumentException("Fulfilment line id is required.", nameof(id));
        if (fulfilmentId == Guid.Empty) throw new ArgumentException("Fulfilment id is required.", nameof(fulfilmentId));
        if (requestItemId == Guid.Empty) throw new ArgumentException("Request item id is required.", nameof(requestItemId));
        if (requestedQuantity <= 0) throw new ArgumentOutOfRangeException(nameof(requestedQuantity));
        Id = id;
        FulfilmentId = fulfilmentId;
        RequestItemId = requestItemId;
        RequestedQuantity = requestedQuantity;
    }

    public Guid Id { get; private set; }
    public Guid FulfilmentId { get; private set; }
    public Guid RequestItemId { get; private set; }
    public decimal RequestedQuantity { get; private set; }
    public decimal FulfilledQuantity { get; private set; }
    public decimal FulfilledValue { get; private set; }
    public DateTimeOffset? LastProgressUtc { get; private set; }
    public string? Notes { get; private set; }
    public decimal RemainingQuantity => Math.Max(0, RequestedQuantity - FulfilledQuantity);
    public bool IsComplete => FulfilledQuantity >= RequestedQuantity;

    public void RecordProgress(decimal quantity, decimal value, DateTimeOffset recordedUtc, string? notes)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Fulfilled quantity must be greater than zero.");
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Fulfilled value cannot be negative.");
        if (FulfilledQuantity + quantity > RequestedQuantity)
        {
            throw new InvalidOperationException("Fulfilment progress cannot exceed the requested quantity.");
        }

        FulfilledQuantity += quantity;
        FulfilledValue += value;
        LastProgressUtc = recordedUtc;
        Notes = string.IsNullOrWhiteSpace(notes) ? Notes : notes.Trim();
    }
}
