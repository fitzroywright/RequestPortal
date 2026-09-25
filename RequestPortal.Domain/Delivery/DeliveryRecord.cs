using System;

namespace RequestPortal.Domain.Delivery;

public enum DeliveryStatus
{
    Ready = 0,
    Delivered = 1,
    PartiallyDelivered = 2,
    Failed = 3
}

public sealed class DeliveryRecord
{
    private DeliveryRecord() { }

    public DeliveryRecord(
        Guid id,
        Guid requestId,
        string documentType,
        string documentReference,
        string snapshotJson,
        string accessTokenHash,
        string recipientName,
        string? deliveryPerson,
        DateTimeOffset createdUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Delivery id is required.", nameof(id));
        if (requestId == Guid.Empty) throw new ArgumentException("Request id is required.", nameof(requestId));
        if (string.IsNullOrWhiteSpace(documentType)) throw new ArgumentException("Document type is required.", nameof(documentType));
        if (string.IsNullOrWhiteSpace(documentReference)) throw new ArgumentException("Document reference is required.", nameof(documentReference));
        if (string.IsNullOrWhiteSpace(snapshotJson)) throw new ArgumentException("Delivery snapshot is required.", nameof(snapshotJson));
        if (string.IsNullOrWhiteSpace(accessTokenHash)) throw new ArgumentException("Access token hash is required.", nameof(accessTokenHash));
        if (string.IsNullOrWhiteSpace(recipientName)) throw new ArgumentException("Recipient is required.", nameof(recipientName));

        Id = id;
        RequestId = requestId;
        DocumentType = documentType.Trim();
        DocumentReference = documentReference.Trim();
        SnapshotJson = snapshotJson;
        AccessTokenHash = accessTokenHash;
        RecipientName = recipientName.Trim();
        DeliveryPerson = Normalize(deliveryPerson);
        Status = DeliveryStatus.Ready;
        CreatedUtc = createdUtc;
    }

    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public string DocumentType { get; private set; } = string.Empty;
    public string DocumentReference { get; private set; } = string.Empty;
    public string SnapshotJson { get; private set; } = string.Empty;
    public string AccessTokenHash { get; private set; } = string.Empty;
    public string RecipientName { get; private set; } = string.Empty;
    public string? DeliveryPerson { get; private set; }
    public DeliveryStatus Status { get; private set; }
    public string? AcknowledgedBy { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset? AcknowledgedUtc { get; private set; }
    public Guid? EvidenceDocumentId { get; private set; }
    public string? SignatureData { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset? SyncedToFfpManagerUtc { get; private set; }

    public void Acknowledge(
        DeliveryStatus status,
        string acknowledgedBy,
        DateTimeOffset acknowledgedUtc,
        string? signatureData,
        Guid? evidenceDocumentId,
        string? notes)
    {
        if (Status != DeliveryStatus.Ready)
        {
            throw new InvalidOperationException("This delivery has already been acknowledged.");
        }

        if (status == DeliveryStatus.Ready)
        {
            throw new ArgumentException("An acknowledgement must have a terminal delivery status.", nameof(status));
        }

        if (string.IsNullOrWhiteSpace(acknowledgedBy))
        {
            throw new ArgumentException("Acknowledging person is required.", nameof(acknowledgedBy));
        }

        Status = status;
        AcknowledgedBy = acknowledgedBy.Trim();
        AcknowledgedUtc = acknowledgedUtc;
        SignatureData = Normalize(signatureData);
        EvidenceDocumentId = evidenceDocumentId;
        Notes = Normalize(notes);
    }

    public void MarkSynced(DateTimeOffset syncedUtc)
    {
        if (Status == DeliveryStatus.Ready)
        {
            throw new InvalidOperationException("An unacknowledged delivery cannot be synced as proof of delivery.");
        }

        SyncedToFfpManagerUtc = syncedUtc;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
