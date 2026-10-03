using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Delivery;
using RequestPortal.Application.Documents;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Delivery;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Delivery;

public sealed class DeliveryService : IDeliveryService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IBusinessAuditWriter auditWriter;
    private readonly IDocumentService documentService;
    private readonly INotificationSender? notificationSender;

    public DeliveryService(
        RequestPortalDbContext dbContext,
        IBusinessAuditWriter auditWriter,
        IDocumentService documentService,
        INotificationSender? notificationSender = null)
    {
        this.dbContext = dbContext;
        this.auditWriter = auditWriter;
        this.documentService = documentService;
        this.notificationSender = notificationSender;
    }

    public async Task<CreateDeliveryResult> CreateAsync(CreateDeliveryCommand command, CancellationToken cancellationToken = default)
    {
        bool requestExists = await dbContext.AssistanceRequests
            .AsNoTracking()
            .AnyAsync(item => item.Id == command.RequestId, cancellationToken);
        if (!requestExists) throw new InvalidOperationException("Request was not found.");

        string externalId = string.IsNullOrWhiteSpace(command.FfpDeliveryOrderId)
            ? command.DocumentReference.Trim()
            : command.FfpDeliveryOrderId.Trim();

        DeliveryRecord? existing = await dbContext.Deliveries
            .SingleOrDefaultAsync(item =>
                item.RequestId == command.RequestId &&
                item.FfpDeliveryOrderId == externalId,
                cancellationToken);

        if (existing is not null)
        {
            existing.UpdateReturnPermission(command.ReturnsPermitted);
            await dbContext.SaveChangesAsync(cancellationToken);
            return new CreateDeliveryResult(existing.Id, string.Empty, false);
        }

        var snapshot = new
        {
            command.DocumentType,
            command.DocumentReference,
            command.RecipientName,
            command.DeliveryPerson,
            Lines = command.Lines,
            DocumentPayload = string.IsNullOrWhiteSpace(command.DocumentPayloadJson)
                ? (JsonElement?)null
                : JsonDocument.Parse(command.DocumentPayloadJson).RootElement.Clone()
        };

        string snapshotJson = JsonSerializer.Serialize(snapshot);
        string accessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string accessTokenHash = HashToken(accessToken);
        DeliveryRecord record = new(
            Guid.NewGuid(),
            command.RequestId,
            command.DocumentType,
            command.DocumentReference,
            snapshotJson,
            accessTokenHash,
            command.RecipientName,
            command.DeliveryPerson,
            DateTimeOffset.UtcNow,
            ffpDeliveryOrderId: externalId,
            returnsPermitted: command.ReturnsPermitted);

        dbContext.Deliveries.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            Guid.Empty,
            "DeliveryCreated",
            "Delivery",
            record.Id,
            null,
            $"request={command.RequestId:D}; deliveryOrder={externalId}",
            cancellationToken);

        bool notificationQueued = false;
        if (notificationSender is not null &&
            !string.IsNullOrWhiteSpace(command.RecipientEmail) &&
            !string.IsNullOrWhiteSpace(command.PublicBaseUrl))
        {
            string acknowledgementUrl =
                $"{command.PublicBaseUrl.TrimEnd('/')}/Delivery/{record.Id:D}?token={Uri.EscapeDataString(accessToken)}";

            try
            {
                await notificationSender.SendAsync(
                    new NotificationEnvelope(
                        command.RecipientEmail.Trim(),
                        $"Delivery acknowledgement required: {command.DocumentReference}",
                        $"A delivery is ready for your acknowledgement.\n\nReference: {command.DocumentReference}\nRecipient: {command.RecipientName}\n\nOpen the secure link below to confirm whether the delivery was received in full, partially delivered, or not delivered:\n{acknowledgementUrl}\n\nThis secure link expires {record.AccessExpiresUtc:u}.",
                        CorrelationId: record.Id.ToString("D")),
                    cancellationToken);

                notificationQueued = true;
                await auditWriter.WriteAsync(
                    Guid.Empty,
                    "DeliveryNotificationQueued",
                    "Delivery",
                    record.Id,
                    null,
                    $"recipient={command.RecipientEmail.Trim()}",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                await auditWriter.WriteAsync(
                    Guid.Empty,
                    "DeliveryNotificationQueueFailed",
                    "Delivery",
                    record.Id,
                    null,
                    $"recipient={command.RecipientEmail.Trim()}; error={ex.GetType().Name}",
                    cancellationToken);
            }
        }

        return new CreateDeliveryResult(record.Id, accessToken, notificationQueued);
    }

    public async Task<DeliveryView?> GetAsync(Guid deliveryId, string accessToken, CancellationToken cancellationToken = default)
    {
        DeliveryRecord? item = await dbContext.Deliveries.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        return item is null ||
               !item.IsAccessActive(DateTimeOffset.UtcNow) ||
               !TokenMatches(item.AccessTokenHash, accessToken)
            ? null
            : ToView(item);
    }

    public async Task<IReadOnlyList<DeliveryView>> GetForRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        DeliveryRecord[] items = await dbContext.Deliveries
            .AsNoTracking()
            .Where(item => item.RequestId == requestId)
            .OrderByDescending(item => item.CreatedUtc)
            .ToArrayAsync(cancellationToken);

        return items.Select(ToView).ToArray();
    }

    public async Task<IReadOnlyList<DeliveryView>> GetForRecipientRequestAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await EnsureRequestAccessAsync(userId, requestId, cancellationToken);
        return await GetForRequestAsync(requestId, cancellationToken);
    }

    public async Task<Guid> UploadEvidenceAsync(
        Guid deliveryId,
        string accessToken,
        string originalFileName,
        string contentType,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        DeliveryRecord delivery = await dbContext.Deliveries
            .AsNoTracking()
            .SingleAsync(item => item.Id == deliveryId, cancellationToken);

        if (!delivery.IsAccessActive(DateTimeOffset.UtcNow) ||
            !TokenMatches(delivery.AccessTokenHash, accessToken))
            throw new UnauthorizedAccessException("The delivery access token is invalid or expired.");

        if (delivery.Status != DeliveryStatus.Ready)
            throw new InvalidOperationException("Evidence can only be uploaded before the delivery acknowledgement is finalized.");

        return await documentService.UploadDeliveryEvidenceAsync(
            delivery.RequestId,
            delivery.Id,
            originalFileName,
            contentType,
            sizeBytes,
            content,
            cancellationToken);
    }

    public async Task AcknowledgeAsync(
        Guid deliveryId,
        string accessToken,
        string status,
        string acknowledgedBy,
        string? signatureData,
        Guid? evidenceDocumentId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse(status, true, out DeliveryStatus deliveryStatus) || deliveryStatus == DeliveryStatus.Ready)
            throw new ArgumentException("Delivery status must be Delivered, PartiallyDelivered, or Failed.", nameof(status));

        DeliveryRecord delivery = await dbContext.Deliveries.SingleAsync(item => item.Id == deliveryId, cancellationToken);
        if (!delivery.IsAccessActive(DateTimeOffset.UtcNow) ||
            !TokenMatches(delivery.AccessTokenHash, accessToken))
            throw new UnauthorizedAccessException("The delivery access token is invalid or expired.");

        delivery.Acknowledge(deliveryStatus, acknowledgedBy, DateTimeOffset.UtcNow, signatureData, evidenceDocumentId, notes);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(Guid.Empty, "DeliveryAcknowledged", "Delivery", delivery.Id, null, $"status={delivery.Status}; acknowledgedBy={delivery.AcknowledgedBy}", cancellationToken);
    }

    public async Task ConfirmReceiptAsync(
        Guid userId,
        Guid requestId,
        Guid deliveryId,
        IReadOnlyList<RecipientConfirmedLine> items,
        IReadOnlyList<DeliveryDiscrepancy> discrepancies,
        CancellationToken cancellationToken = default)
    {
        await EnsureRequestAccessAsync(userId, requestId, cancellationToken);

        DeliveryRecord delivery = await dbContext.Deliveries
            .SingleAsync(item => item.Id == deliveryId && item.RequestId == requestId, cancellationToken);

        DeliveryLineSnapshot[] expected = ReadLines(delivery.SnapshotJson);
        if (items.Count != expected.Length)
            throw new InvalidOperationException("Every delivered item must be reviewed before receipt is confirmed.");

        for (int i = 0; i < expected.Length; i++)
        {
            RecipientConfirmedLine confirmed = items[i];
            DeliveryLineSnapshot line = expected[i];
            if (!string.Equals(confirmed.Description, line.Description, StringComparison.Ordinal) ||
                confirmed.ExpectedQuantity != line.Quantity ||
                (!string.IsNullOrWhiteSpace(line.FfpDeliveryItemId) && confirmed.FfpDeliveryItemId != line.FfpDeliveryItemId) ||
                confirmed.ReceivedQuantity < 0 ||
                confirmed.ReceivedQuantity > line.Quantity)
                throw new InvalidOperationException("The receipt confirmation does not match the delivery snapshot.");
        }

        string confirmationJson = JsonSerializer.Serialize(items);
        string? discrepancyJson = discrepancies.Count == 0 ? null : JsonSerializer.Serialize(discrepancies);
        delivery.ConfirmRecipientReceipt(userId, DateTimeOffset.UtcNow, confirmationJson, discrepancyJson);

        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            userId,
            "DeliveryReceiptConfirmed",
            "Delivery",
            delivery.Id,
            null,
            $"request={requestId:D}; discrepancies={discrepancies.Count}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<RecipientReceiptConfirmation>> GetUnsyncedRecipientConfirmationsAsync(
        CancellationToken cancellationToken = default)
    {
        DeliveryRecord[] records = await dbContext.Deliveries
            .AsNoTracking()
            .Where(item => item.RecipientConfirmedUtc != null && item.RecipientConfirmationSyncedUtc == null)
            .OrderBy(item => item.RecipientConfirmedUtc)
            .ToArrayAsync(cancellationToken);

        return records.Select(record => new RecipientReceiptConfirmation(
            record.Id,
            record.RequestId,
            record.FfpDeliveryOrderId,
            record.ConfirmedByUserId!.Value,
            record.RecipientConfirmedUtc!.Value,
            DeserializeConfirmedLines(record.RecipientConfirmationJson),
            DeserializeDiscrepancies(record.RecipientDiscrepancyJson)))
            .ToArray();
    }

    public async Task MarkRecipientConfirmationSyncedAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        DeliveryRecord delivery = await dbContext.Deliveries.SingleAsync(item => item.Id == deliveryId, cancellationToken);
        delivery.MarkRecipientConfirmationSynced(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(Guid.Empty, "DeliveryReceiptSyncedToFfpManager", "Delivery", delivery.Id, null, null, cancellationToken);
    }

    public async Task<DeliveryReturnView> InitiateReturnAsync(
        Guid userId,
        Guid requestId,
        Guid deliveryId,
        string itemDescription,
        decimal quantity,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await EnsureRequestAccessAsync(userId, requestId, cancellationToken);

        DeliveryRecord delivery = await dbContext.Deliveries
            .AsNoTracking()
            .SingleAsync(item => item.Id == deliveryId && item.RequestId == requestId, cancellationToken);

        if (!delivery.ReturnsPermitted)
            throw new InvalidOperationException("Returns are not currently available for this delivery.");
        if (!delivery.RecipientConfirmedUtc.HasValue)
            throw new InvalidOperationException("Receipt must be confirmed before a return can be requested.");

        RecipientConfirmedLine? confirmedLine = DeserializeConfirmedLines(delivery.RecipientConfirmationJson)
            .FirstOrDefault(item => string.Equals(item.Description, itemDescription, StringComparison.Ordinal));
        if (confirmedLine is null || quantity <= 0 || quantity > confirmedLine.ReceivedQuantity)
            throw new InvalidOperationException("Return quantity must be within the quantity confirmed as received.");

        decimal alreadyOpen = await dbContext.DeliveryReturns
            .AsNoTracking()
            .Where(item =>
                item.DeliveryId == deliveryId &&
                item.ItemDescription == itemDescription &&
                item.Status != "Rejected" &&
                item.Status != "Cancelled")
            .SumAsync(item => (decimal?)item.Quantity, cancellationToken) ?? 0m;

        if (alreadyOpen + quantity > confirmedLine.ReceivedQuantity)
            throw new InvalidOperationException("The requested return quantity exceeds the quantity available to return.");

        DeliveryReturnRequest returnRequest = new(
            Guid.NewGuid(),
            requestId,
            deliveryId,
            confirmedLine.FfpDeliveryItemId,
            itemDescription,
            quantity,
            reason,
            userId,
            DateTimeOffset.UtcNow);

        dbContext.DeliveryReturns.Add(returnRequest);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            userId,
            "DeliveryReturnRequested",
            "DeliveryReturn",
            returnRequest.Id,
            null,
            $"delivery={deliveryId:D}; quantity={quantity}",
            cancellationToken);

        return ToReturnView(returnRequest);
    }

    public async Task<IReadOnlyList<DeliveryReturnView>> GetReturnsForRequestAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await EnsureRequestAccessAsync(userId, requestId, cancellationToken);
        return await dbContext.DeliveryReturns
            .AsNoTracking()
            .Where(item => item.RequestId == requestId)
            .OrderByDescending(item => item.RequestedUtc)
            .Select(item => new DeliveryReturnView(
                item.Id,
                item.RequestId,
                item.DeliveryId,
                item.ItemDescription,
                item.Quantity,
                item.Reason,
                item.RequestedByUserId,
                item.RequestedUtc,
                item.Status,
                item.FfpReturnId,
                item.StatusDetail,
                item.StatusUpdatedUtc,
                item.SyncedToFfpManagerUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryReturnSync>> GetUnsyncedReturnsAsync(CancellationToken cancellationToken = default)
    {
        return await (
            from item in dbContext.DeliveryReturns.AsNoTracking()
            join delivery in dbContext.Deliveries.AsNoTracking()
                on item.DeliveryId equals delivery.Id
            where item.SyncedToFfpManagerUtc == null
            orderby item.RequestedUtc
            select new DeliveryReturnSync(
                item.Id,
                item.RequestId,
                item.DeliveryId,
                delivery.FfpDeliveryOrderId,
                item.FfpDeliveryItemId,
                item.ItemDescription,
                item.Quantity,
                item.Reason,
                item.RequestedByUserId,
                item.RequestedUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task MarkReturnSyncedAsync(Guid returnId, string? ffpReturnId, CancellationToken cancellationToken = default)
    {
        DeliveryReturnRequest item = await dbContext.DeliveryReturns.SingleAsync(row => row.Id == returnId, cancellationToken);
        item.MarkSynced(ffpReturnId, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(Guid.Empty, "DeliveryReturnSyncedToFfpManager", "DeliveryReturn", item.Id, null, $"ffpReturnId={ffpReturnId}", cancellationToken);
    }

    public async Task ApplyReturnStatusAsync(
        Guid returnId,
        string status,
        string? detail,
        DateTimeOffset changedUtc,
        CancellationToken cancellationToken = default)
    {
        DeliveryReturnRequest item = await dbContext.DeliveryReturns.SingleAsync(row => row.Id == returnId, cancellationToken);
        item.ApplyAuthoritativeStatus(status, detail, changedUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(Guid.Empty, "DeliveryReturnStatusReceived", "DeliveryReturn", item.Id, null, $"status={status}", cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryConfirmation>> GetUnsyncedConfirmationsAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.Deliveries.AsNoTracking()
            .Where(item => item.Status != DeliveryStatus.Ready && item.SyncedToFfpManagerUtc == null)
            .OrderBy(item => item.AcknowledgedUtc)
            .Select(item => new DeliveryConfirmation(
                item.Id,
                item.RequestId,
                item.DocumentReference,
                item.Status.ToString(),
                item.AcknowledgedBy!,
                item.AcknowledgedUtc!.Value,
                item.EvidenceDocumentId,
                item.Notes))
            .ToListAsync(cancellationToken);
    }

    public async Task MarkSyncedAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        DeliveryRecord delivery = await dbContext.Deliveries.SingleAsync(item => item.Id == deliveryId, cancellationToken);
        delivery.MarkSynced(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(Guid.Empty, "DeliverySyncedToFfpManager", "Delivery", delivery.Id, null, null, cancellationToken);
    }

    private async Task EnsureRequestAccessAsync(Guid userId, Guid requestId, CancellationToken cancellationToken)
    {
        var request = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Where(item => item.Id == requestId)
            .Select(item => new { item.CreatedByUserId, item.OrganizationId })
            .SingleOrDefaultAsync(cancellationToken);

        if (request is null)
            throw new InvalidOperationException("Request was not found.");

        if (request.CreatedByUserId == userId)
            return;

        bool member = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(item =>
                item.OrganizationId == request.OrganizationId &&
                item.UserId == userId &&
                item.Status == MembershipStatus.Approved,
                cancellationToken);

        if (!member)
            throw new UnauthorizedAccessException("You do not have access to this request.");
    }

    private static DeliveryLineSnapshot[] ReadLines(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Lines", out JsonElement lines) ||
            lines.ValueKind != JsonValueKind.Array)
            return Array.Empty<DeliveryLineSnapshot>();

        return lines.EnumerateArray()
            .Select(line => new DeliveryLineSnapshot(
                line.TryGetProperty("Description", out JsonElement description) ? description.GetString() ?? string.Empty : string.Empty,
                line.TryGetProperty("Quantity", out JsonElement quantity) && quantity.TryGetDecimal(out decimal value) ? value : 0m,
                line.TryGetProperty("UnitOfMeasure", out JsonElement unit) ? unit.GetString() : null,
                line.TryGetProperty("FfpDeliveryItemId", out JsonElement itemId) ? itemId.GetString() : null))
            .ToArray();
    }

    private static IReadOnlyList<RecipientConfirmedLine> DeserializeConfirmedLines(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? Array.Empty<RecipientConfirmedLine>()
            : JsonSerializer.Deserialize<RecipientConfirmedLine[]>(json) ?? Array.Empty<RecipientConfirmedLine>();

    private static IReadOnlyList<DeliveryDiscrepancy> DeserializeDiscrepancies(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? Array.Empty<DeliveryDiscrepancy>()
            : JsonSerializer.Deserialize<DeliveryDiscrepancy[]>(json) ?? Array.Empty<DeliveryDiscrepancy>();

    private static bool TokenMatches(string expectedHash, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        byte[] expected = Convert.FromHexString(expectedHash);
        byte[] actual = Convert.FromHexString(HashToken(token));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static DeliveryView ToView(DeliveryRecord item) =>
        new(
            item.Id,
            item.RequestId,
            item.DocumentType,
            item.DocumentReference,
            item.RecipientName,
            item.DeliveryPerson,
            item.Status.ToString(),
            item.CreatedUtc,
            item.SnapshotJson,
            item.AcknowledgedBy,
            item.AcknowledgedUtc,
            item.EvidenceDocumentId,
            item.Notes,
            item.SyncedToFfpManagerUtc,
            item.FfpDeliveryOrderId,
            item.ReturnsPermitted,
            item.ConfirmedByUserId,
            item.RecipientConfirmedUtc,
            DeserializeConfirmedLines(item.RecipientConfirmationJson),
            DeserializeDiscrepancies(item.RecipientDiscrepancyJson));

    private static DeliveryReturnView ToReturnView(DeliveryReturnRequest item) =>
        new(
            item.Id,
            item.RequestId,
            item.DeliveryId,
            item.ItemDescription,
            item.Quantity,
            item.Reason,
            item.RequestedByUserId,
            item.RequestedUtc,
            item.Status,
            item.FfpReturnId,
            item.StatusDetail,
            item.StatusUpdatedUtc,
            item.SyncedToFfpManagerUtc);
}
