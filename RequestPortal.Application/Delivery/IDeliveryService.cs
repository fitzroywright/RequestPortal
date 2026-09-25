using System;
using System.Collections.Generic;
using System.IO;
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
    string? DocumentPayloadJson,
    string? RecipientEmail = null,
    string? PublicBaseUrl = null);

public sealed record CreateDeliveryResult(
    Guid DeliveryId,
    string AccessToken,
    bool NotificationQueued = false);

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
    Task<CreateDeliveryResult> CreateAsync(CreateDeliveryCommand command, CancellationToken cancellationToken = default);
    Task<DeliveryView?> GetAsync(Guid deliveryId, string accessToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryView>> GetForRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<Guid> UploadEvidenceAsync(Guid deliveryId, string accessToken, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(Guid deliveryId, string accessToken, string status, string acknowledgedBy, string? signatureData, Guid? evidenceDocumentId, string? notes, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryConfirmation>> GetUnsyncedConfirmationsAsync(CancellationToken cancellationToken = default);
    Task MarkSyncedAsync(Guid deliveryId, CancellationToken cancellationToken = default);
}
