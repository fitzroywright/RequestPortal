using System;

namespace RequestPortal.Domain.Delivery;

public sealed class DeliveryReturnRequest
{
    private DeliveryReturnRequest() { }

    public DeliveryReturnRequest(
        Guid id,
        Guid requestId,
        Guid deliveryId,
        string? ffpDeliveryItemId,
        string itemDescription,
        decimal quantity,
        string reason,
        Guid requestedByUserId,
        DateTimeOffset requestedUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Return id is required.", nameof(id));
        if (requestId == Guid.Empty) throw new ArgumentException("Request id is required.", nameof(requestId));
        if (deliveryId == Guid.Empty) throw new ArgumentException("Delivery id is required.", nameof(deliveryId));
        if (string.IsNullOrWhiteSpace(itemDescription)) throw new ArgumentException("Returned item is required.", nameof(itemDescription));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Return reason is required.", nameof(reason));
        if (requestedByUserId == Guid.Empty) throw new ArgumentException("Recipient user is required.", nameof(requestedByUserId));

        Id = id;
        RequestId = requestId;
        DeliveryId = deliveryId;
        FfpDeliveryItemId = Normalize(ffpDeliveryItemId);
        ItemDescription = itemDescription.Trim();
        Quantity = quantity;
        Reason = reason.Trim();
        RequestedByUserId = requestedByUserId;
        RequestedUtc = requestedUtc;
        Status = "Requested";
    }

    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public Guid DeliveryId { get; private set; }
    public string? FfpDeliveryItemId { get; private set; }
    public string ItemDescription { get; private set; } = string.Empty;
    public decimal Quantity { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public Guid RequestedByUserId { get; private set; }
    public DateTimeOffset RequestedUtc { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public string? FfpReturnId { get; private set; }
    public string? StatusDetail { get; private set; }
    public DateTimeOffset? StatusUpdatedUtc { get; private set; }
    public DateTimeOffset? SyncedToFfpManagerUtc { get; private set; }

    public void MarkSynced(string? ffpReturnId, DateTimeOffset syncedUtc)
    {
        FfpReturnId = Normalize(ffpReturnId);
        SyncedToFfpManagerUtc = syncedUtc;
    }

    public void ApplyAuthoritativeStatus(string status, string? detail, DateTimeOffset changedUtc)
    {
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("Return status is required.", nameof(status));

        Status = status.Trim();
        StatusDetail = Normalize(detail);
        StatusUpdatedUtc = changedUtc;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
