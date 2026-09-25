using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Delivery;
using RequestPortal.Domain.Delivery;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Delivery;

public sealed class DeliveryService : IDeliveryService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IBusinessAuditWriter auditWriter;
    private readonly IDocumentService documentService;

    public DeliveryService(
        RequestPortalDbContext dbContext,
        IBusinessAuditWriter auditWriter,
        IDocumentService documentService)
    {
        this.dbContext = dbContext;
        this.auditWriter = auditWriter;
        this.documentService = documentService;
    }

    public async Task<CreateDeliveryResult> CreateAsync(CreateDeliveryCommand command, CancellationToken cancellationToken = default)
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
            DateTimeOffset.UtcNow);

        dbContext.Deliveries.Add(record);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(Guid.Empty, "DeliveryCreated", "Delivery", record.Id, null, $"request={command.RequestId:D}; document={command.DocumentReference}", cancellationToken);
        return new CreateDeliveryResult(record.Id, accessToken);
    }

    public async Task<DeliveryView?> GetAsync(Guid deliveryId, string accessToken, CancellationToken cancellationToken = default)
    {
        DeliveryRecord? item = await dbContext.Deliveries.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == deliveryId, cancellationToken);
        return item is null || !TokenMatches(item.AccessTokenHash, accessToken) ? null : ToView(item);
    }

    public async Task<Guid> UploadEvidenceAsync(
        Guid deliveryId,
        string accessToken,
        string originalFileName,
        string contentType,
        long sizeBytes,
        System.IO.Stream content,
        CancellationToken cancellationToken = default)
    {
        DeliveryRecord delivery = await dbContext.Deliveries
            .AsNoTracking()
            .SingleAsync(item => item.Id == deliveryId, cancellationToken);
        if (!TokenMatches(delivery.AccessTokenHash, accessToken))
        {
            throw new UnauthorizedAccessException("The delivery access token is invalid.");
        }
        if (delivery.Status != DeliveryStatus.Ready)
        {
            throw new InvalidOperationException("Evidence cannot be added after a delivery has been acknowledged.");
        }

        Guid documentId = await documentService.UploadDeliveryEvidenceAsync(
            delivery.RequestId,
            originalFileName,
            contentType,
            sizeBytes,
            content,
            cancellationToken);
        await auditWriter.WriteAsync(
            Guid.Empty,
            "DeliveryEvidenceLinked",
            "Delivery",
            delivery.Id,
            null,
            $"document={documentId:D}",
            cancellationToken);
        return documentId;
    }

    public async Task AcknowledgeAsync(Guid deliveryId, string accessToken, string status, string acknowledgedBy, string? signatureData, Guid? evidenceDocumentId, string? notes, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse(status, true, out DeliveryStatus deliveryStatus) || deliveryStatus == DeliveryStatus.Ready)
        {
            throw new ArgumentException("Delivery status must be Delivered, PartiallyDelivered, or Failed.", nameof(status));
        }

        DeliveryRecord delivery = await dbContext.Deliveries.SingleAsync(item => item.Id == deliveryId, cancellationToken);
        if (!TokenMatches(delivery.AccessTokenHash, accessToken)) throw new UnauthorizedAccessException("The delivery access token is invalid.");
        delivery.Acknowledge(deliveryStatus, acknowledgedBy, DateTimeOffset.UtcNow, signatureData, evidenceDocumentId, notes);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(Guid.Empty, "DeliveryAcknowledged", "Delivery", delivery.Id, null, $"status={delivery.Status}; acknowledgedBy={delivery.AcknowledgedBy}", cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryView>> GetForRequestAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        DeliveryRecord[] deliveries = await dbContext.Deliveries
            .AsNoTracking()
            .Where(item => item.RequestId == requestId)
            .OrderByDescending(item => item.CreatedUtc)
            .ToArrayAsync(cancellationToken);
        return deliveries.Select(ToView).ToArray();
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

    private static bool TokenMatches(string expectedHash, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        byte[] expected = Convert.FromHexString(expectedHash);
        byte[] actual = Convert.FromHexString(HashToken(token));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static DeliveryView ToView(DeliveryRecord item)
    {
        IReadOnlyList<DeliveryLineSnapshot> lines = Array.Empty<DeliveryLineSnapshot>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(item.SnapshotJson);
            if (document.RootElement.TryGetProperty("Lines", out JsonElement element))
            {
                lines = JsonSerializer.Deserialize<DeliveryLineSnapshot[]>(element.GetRawText()) ?? Array.Empty<DeliveryLineSnapshot>();
            }
        }
        catch (JsonException)
        {
        }

        return new DeliveryView(
            item.Id,
            item.RequestId,
            item.DocumentType,
            item.DocumentReference,
            item.RecipientName,
            item.DeliveryPerson,
            item.Status.ToString(),
            item.CreatedUtc,
            item.SnapshotJson,
            lines,
            item.AcknowledgedBy,
            item.AcknowledgedUtc,
            item.EvidenceDocumentId,
            item.Notes,
            item.SyncedToFfpManagerUtc);
    }
}
