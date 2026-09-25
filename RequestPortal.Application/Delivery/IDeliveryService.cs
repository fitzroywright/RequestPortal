using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Delivery;

public sealed record DeliveryLineSnapshot(string Description, decimal Quantity, string? UnitOfMeasure);

public sealed record CreateDeliveryCommand(
    Guid RequestId,
    string DocumentType,
    string DocumentReference,
    string RecipientName,
    string? DeliveryPerson,
    IReadOnlyList<DeliveryLineSnapshot> Lines,
    string? DocumentPayloadJson);

public sealed record DeliveryView(
    Guid Id,
    Guid RequestId,
    string DocumentType,
    string DocumentReference,
    string RecipientName,
    string? DeliveryPerson,
    string Status,
    DateTimeOffset CreatedUtc,
    string SnapshotJson,
    string? AcknowledgedBy,
    DateTimeOffset? AcknowledgedUtc,
    Guid? EvidenceDocumentId,
    string? Notes,
    DateTimeOffset? SyncedToFfpManagerUtc);

public sealed record DeliveryConfirmation(
    Guid DeliveryId,
    Guid RequestId,
    string DocumentReference,
    string Status,
    string AcknowledgedBy,
    DateTimeOffset AcknowledgedUtc,
    Guid? EvidenceDocumentId,
    string? Notes);

public interface IDeliveryService
{
    Task<Guid> CreateAsync(CreateDeliveryCommand command, CancellationToken cancellationToken = default);
    Task<DeliveryView?> GetAsync(Guid deliveryId, CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(Guid deliveryId, string status, string acknowledgedBy, string? signatureData, Guid? evidenceDocumentId, string? notes, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryConfirmation>> GetUnsyncedConfirmationsAsync(CancellationToken cancellationToken = default);
    Task MarkSyncedAsync(Guid deliveryId, CancellationToken cancellationToken = default);
}
