using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Delivery;
using RequestPortal.Domain.Delivery;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Delivery;

public sealed class DeliveryService : IDeliveryService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IBusinessAuditWriter auditWriter;

    public DeliveryService(RequestPortalDbContext dbContext, IBusinessAuditWriter auditWriter)
    {
        this.dbContext = dbContext;
        this.auditWriter = auditWriter;
    }

    public async Task<Guid> CreateAsync(CreateDeliveryCommand command, CancellationToken cancellationToken = default)
    {
        bool requestExists = await dbContext.AssistanceRequests
            .AsNoTracking()
            .AnyAsync(item => item.Id == command.RequestId, cancellationToken);
        if (!requestExists) throw new InvalidOperationException("Request was not found.");

        var snapshot = new
        {
            command.DocumentType,
            command.DocumentReference,
            command.RecipientName,
            command.DeliveryPerson,
            Lines = command.Lines,
            DocumentPayload = string.IsNullOrWhiteSpace(command.DocumentPayloadJson)
                ? null
                : JsonDocument.Parse(command.DocumentPayloadJson).RootElement.Clone()
        };

        string snapshotJson = JsonSerializer.Serialize(snapshot);
        DeliveryRecord record = new(
            Guid.NewGuid(),
            command.RequestId,
            command.DocumentType,
            command.DocumentReference,
            snapshotJson,
            command.RecipientName,
            command.DeliveryPerson,
            DateTimeOffset.UtcNow);

        dbContext.Deliveries.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(null, "DeliveryCreated", "Delivery", record.Id, null, $"request={command.RequestId:D}; document={command.DocumentReference}", cancellationToken);
        return record.Id;
    }

    public async Task<DeliveryView?> GetAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        DeliveryRecord? item = await dbContext.Deliveries.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        return item is null ? null : ToView(item);
    }

    public async Task AcknowledgeAsync(Guid deliveryId, string status, string acknowledgedBy, string? signatureData, Guid? evidenceDocumentId, string? notes, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse(status, true, out DeliveryStatus deliveryStatus) || deliveryStatus == DeliveryStatus.Ready)
        {
            throw new ArgumentException("Delivery status must be Delivered, PartiallyDelivered, or Failed.", nameof(status));
        }

        DeliveryRecord delivery = await dbContext.Deliveries.SingleAsync(item => item.Id == deliveryId, cancellationToken);
        delivery.Acknowledge(deliveryStatus, acknowledgedBy, DateTimeOffset.UtcNow, signatureData, evidenceDocumentId, notes);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(null, "DeliveryAcknowledged", "Delivery", delivery.Id, null, $"status={delivery.Status}; acknowledgedBy={delivery.AcknowledgedBy}", cancellationToken);
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
        await auditWriter.WriteAsync(null, "DeliverySyncedToFfpManager", "Delivery", delivery.Id, null, null, cancellationToken);
    }

    private static DeliveryView ToView(DeliveryRecord item) =>
        new(item.Id, item.RequestId, item.DocumentType, item.DocumentReference, item.RecipientName, item.DeliveryPerson, item.Status.ToString(), item.CreatedUtc, item.SnapshotJson, item.AcknowledgedBy, item.AcknowledgedUtc, item.EvidenceDocumentId, item.Notes, item.SyncedToFfpManagerUtc);
}
