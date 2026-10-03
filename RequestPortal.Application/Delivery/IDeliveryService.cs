using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Delivery;

public sealed record DeliveryLineSnapshot(string Description, decimal Quantity, string? UnitOfMeasure, string? FfpDeliveryItemId = null);
public sealed record RecipientConfirmedLine(string Description, decimal ExpectedQuantity, decimal ReceivedQuantity, string? UnitOfMeasure, string? FfpDeliveryItemId = null);
public sealed record DeliveryDiscrepancy(string Type, string? ItemDescription, decimal? ExpectedQuantity, decimal? ReceivedQuantity, string? Detail);

public sealed record CreateDeliveryCommand(
    Guid RequestId,
    string DocumentType,
    string DocumentReference,
    string RecipientName,
    string? DeliveryPerson,
    IReadOnlyList<DeliveryLineSnapshot> Lines,
    string? DocumentPayloadJson,
    string? RecipientEmail = null,
    string? PublicBaseUrl = null,
    string? FfpDeliveryOrderId = null,
    bool ReturnsPermitted = false);

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
    DateTimeOffset? SyncedToFfpManagerUtc,
    string? FfpDeliveryOrderId = null,
    bool ReturnsPermitted = false,
    Guid? ConfirmedByUserId = null,
    DateTimeOffset? RecipientConfirmedUtc = null,
    IReadOnlyList<RecipientConfirmedLine>? ConfirmedLines = null,
    IReadOnlyList<DeliveryDiscrepancy>? Discrepancies = null);

public sealed record DeliveryConfirmation(
    Guid DeliveryId,
    Guid RequestId,
    string DocumentReference,
    string Status,
    string AcknowledgedBy,
    DateTimeOffset AcknowledgedUtc,
    Guid? EvidenceDocumentId,
    string? Notes);

public sealed record RecipientReceiptConfirmation(
    Guid DeliveryId,
    Guid RequestId,
    string FfpDeliveryOrderId,
    Guid ConfirmedByUserId,
    DateTimeOffset ConfirmedUtc,
    IReadOnlyList<RecipientConfirmedLine> Items,
    IReadOnlyList<DeliveryDiscrepancy> Discrepancies);

public sealed record DeliveryReturnView(
    Guid Id,
    Guid RequestId,
    Guid DeliveryId,
    string ItemDescription,
    decimal Quantity,
    string Reason,
    Guid RequestedByUserId,
    DateTimeOffset RequestedUtc,
    string Status,
    string? FfpReturnId,
    string? StatusDetail,
    DateTimeOffset? StatusUpdatedUtc,
    DateTimeOffset? SyncedToFfpManagerUtc);

public sealed record DeliveryReturnSync(
    Guid ReturnId,
    Guid RequestId,
    Guid DeliveryId,
    string FfpDeliveryOrderId,
    string? FfpDeliveryItemId,
    string ItemDescription,
    decimal Quantity,
    string Reason,
    Guid RequestedByUserId,
    DateTimeOffset RequestedUtc);

public interface IDeliveryService
{
    Task<CreateDeliveryResult> CreateAsync(CreateDeliveryCommand command, CancellationToken cancellationToken = default);
    Task<DeliveryView?> GetAsync(Guid deliveryId, string accessToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryView>> GetForRequestAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryView>> GetForRecipientRequestAsync(Guid userId, Guid requestId, CancellationToken cancellationToken = default);
    Task<Guid> UploadEvidenceAsync(Guid deliveryId, string accessToken, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(Guid deliveryId, string accessToken, string status, string acknowledgedBy, string? signatureData, Guid? evidenceDocumentId, string? notes, CancellationToken cancellationToken = default);
    Task ConfirmReceiptAsync(Guid userId, Guid requestId, Guid deliveryId, IReadOnlyList<RecipientConfirmedLine> items, IReadOnlyList<DeliveryDiscrepancy> discrepancies, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecipientReceiptConfirmation>> GetUnsyncedRecipientConfirmationsAsync(CancellationToken cancellationToken = default);
    Task MarkRecipientConfirmationSyncedAsync(Guid deliveryId, CancellationToken cancellationToken = default);
    Task<DeliveryReturnView> InitiateReturnAsync(Guid userId, Guid requestId, Guid deliveryId, string itemDescription, decimal quantity, string reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryReturnView>> GetReturnsForRequestAsync(Guid userId, Guid requestId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryReturnSync>> GetUnsyncedReturnsAsync(CancellationToken cancellationToken = default);
    Task MarkReturnSyncedAsync(Guid returnId, string? ffpReturnId, CancellationToken cancellationToken = default);
    Task ApplyReturnStatusAsync(Guid returnId, string status, string? detail, DateTimeOffset changedUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryConfirmation>> GetUnsyncedConfirmationsAsync(CancellationToken cancellationToken = default);
    Task MarkSyncedAsync(Guid deliveryId, CancellationToken cancellationToken = default);
}
