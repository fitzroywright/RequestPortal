using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Common.Storage;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Portal;
using RequestPortal.Application.Security;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Documents;

public sealed class DocumentService : IDocumentService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IOrganizationAccessService organizationAccessService;
    private readonly IFileStorage fileStorage;
    private readonly IClock clock;
    private readonly IDocumentUploadSecurity uploadSecurity;
    private readonly IMalwareScanner malwareScanner;
    private readonly IBusinessAuditWriter auditWriter;
    private readonly IRequestPortalOperationsFlowPublisher operationsFlowPublisher;

    public DocumentService(
        RequestPortalDbContext dbContext,
        IOrganizationAccessService organizationAccessService,
        IFileStorage fileStorage,
        IClock clock,
        IDocumentUploadSecurity uploadSecurity,
        IMalwareScanner malwareScanner,
        IBusinessAuditWriter auditWriter,
        IRequestPortalOperationsFlowPublisher operationsFlowPublisher)
    {
        this.dbContext = dbContext;
        this.organizationAccessService = organizationAccessService;
        this.fileStorage = fileStorage;
        this.clock = clock;
        this.uploadSecurity = uploadSecurity;
        this.malwareScanner = malwareScanner;
        this.auditWriter = auditWriter;
        this.operationsFlowPublisher = operationsFlowPublisher;
    }

    public async Task<Guid> UploadForRequestAsync(Guid userId, Guid requestId, DocumentType documentType, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default)
    {
        Guid organizationId = await dbContext.AssistanceRequests.AsNoTracking().Where(request => request.Id == requestId).Select(request => request.OrganizationId).SingleAsync(cancellationToken);
        await EnsureApprovedMemberAsync(userId, organizationId, cancellationToken);
        await uploadSecurity.ValidateAsync(originalFileName, contentType, sizeBytes, content, cancellationToken);
        await malwareScanner.ScanAsync(content, cancellationToken);
        Guid documentId = await StoreMetadataAsync(userId, DocumentOwnerType.AssistanceRequest, requestId, organizationId, documentType, originalFileName, contentType, sizeBytes, content, cancellationToken);
        await AuditAsync(userId, "DocumentUploaded", documentId, organizationId, $"Request document type {documentType}; size {sizeBytes} bytes.", cancellationToken);
        operationsFlowPublisher.TryEnqueue(
            requestId,
            "Documents",
            clock.UtcNow,
            $"Document {documentId:D} persisted and metadata committed.");
        return documentId;
    }

    public async Task<Guid> UploadDeliveryEvidenceAsync(Guid requestId, Guid deliveryId, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default)
    {
        Guid organizationId = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Where(request => request.Id == requestId)
            .Select(request => request.OrganizationId)
            .SingleAsync(cancellationToken);

        await uploadSecurity.ValidateAsync(originalFileName, contentType, sizeBytes, content, cancellationToken);
        await malwareScanner.ScanAsync(content, cancellationToken);
        Guid documentId = await StoreMetadataAsync(
            Guid.Empty,
            DocumentOwnerType.AssistanceRequest,
            requestId,
            organizationId,
            DocumentType.DeliveryEvidence,
            originalFileName,
            contentType,
            sizeBytes,
            content,
            cancellationToken);

        await AuditAsync(
            Guid.Empty,
            "DeliveryEvidenceUploaded",
            documentId,
            organizationId,
            $"Delivery={deliveryId:D}; size={sizeBytes} bytes.",
            cancellationToken);

        operationsFlowPublisher.TryEnqueue(
            requestId,
            "Delivery",
            clock.UtcNow,
            $"Delivery evidence {documentId:D} persisted for delivery {deliveryId:D}.");

        return documentId;
    }

    public async Task<Guid> UploadForOrganizationAsync(Guid userId, Guid organizationId, DocumentType documentType, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default)
    {
        Organization organization = await dbContext.Organizations.AsNoTracking().SingleAsync(item => item.Id == organizationId, cancellationToken);
        bool isApprovedMember = await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken);
        if (organization.RegisteredByUserId != userId && !isApprovedMember) throw new UnauthorizedAccessException("The current user cannot upload documents for this organization.");
        await uploadSecurity.ValidateAsync(originalFileName, contentType, sizeBytes, content, cancellationToken);
        await malwareScanner.ScanAsync(content, cancellationToken);
        Guid documentId = await StoreMetadataAsync(userId, DocumentOwnerType.Organization, organizationId, organizationId, documentType, originalFileName, contentType, sizeBytes, content, cancellationToken);
        await AuditAsync(userId, "DocumentUploaded", documentId, organizationId, $"Organization document type {documentType}; size {sizeBytes} bytes.", cancellationToken);
        return documentId;
    }

    public async Task<IReadOnlyList<DocumentSummary>> GetRequestDocumentsAsync(Guid userId, Guid requestId, bool isStaff, CancellationToken cancellationToken = default)
    {
        Guid organizationId = await dbContext.AssistanceRequests.AsNoTracking().Where(request => request.Id == requestId).Select(request => request.OrganizationId).SingleAsync(cancellationToken);
        if (!isStaff) await EnsureApprovedMemberAsync(userId, organizationId, cancellationToken);
        return await QueryDocumentsAsync(DocumentOwnerType.AssistanceRequest, requestId, cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentSummary>> GetOrganizationDocumentsAsync(Guid userId, Guid organizationId, bool isStaff, CancellationToken cancellationToken = default)
    {
        if (!isStaff)
        {
            Organization organization = await dbContext.Organizations.AsNoTracking().SingleAsync(item => item.Id == organizationId, cancellationToken);
            bool isApprovedMember = await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken);
            if (organization.RegisteredByUserId != userId && !isApprovedMember) throw new UnauthorizedAccessException("The current user cannot access documents for this organization.");
        }
        return await QueryDocumentsAsync(DocumentOwnerType.Organization, organizationId, cancellationToken);
    }

    public async Task<DocumentDownload?> DownloadAsync(Guid userId, Guid documentId, bool isStaff, CancellationToken cancellationToken = default)
    {
        StoredDocument? document = await dbContext.Documents.AsNoTracking().SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);
        if (document is null) return null;
        if (!isStaff) await EnsureApprovedMemberAsync(userId, document.OrganizationId, cancellationToken);
        Stream stream = await fileStorage.OpenReadAsync(document.StorageKey, cancellationToken);
        await AuditAsync(
            userId,
            "DocumentDownloaded",
            document.Id,
            document.OrganizationId,
            $"File={document.OriginalFileName}; staff={isStaff}",
            cancellationToken);
        return new DocumentDownload(stream, document.ContentType, document.OriginalFileName);
    }

    public async Task VerifyAsync(Guid staffUserId, Guid documentId, string? notes, CancellationToken cancellationToken = default)
    {
        StoredDocument document = await dbContext.Documents.SingleAsync(item => item.Id == documentId, cancellationToken);
        document.Verify(staffUserId, clock.UtcNow, notes);

        if (document.OwnerType == DocumentOwnerType.Organization &&
            document.DocumentType == DocumentType.OrganizationRegistrationCertificate)
        {
            Guid messageId = CreateDeterministicGuid(document.Id, "OrganizationRegistrationVerified");
            bool alreadyQueued = await dbContext.OutboxMessages
                .AsNoTracking()
                .AnyAsync(message => message.Id == messageId, cancellationToken);
            if (!alreadyQueued)
            {
                OrganizationRegistrationVerifiedMessage message = new(
                    messageId,
                    clock.UtcNow,
                    document.OrganizationId,
                    document.Id);
                dbContext.OutboxMessages.Add(new OutboxMessage
                {
                    Id = message.MessageId,
                    MessageType = typeof(OrganizationRegistrationVerifiedMessage).FullName
                        ?? nameof(OrganizationRegistrationVerifiedMessage),
                    Payload = JsonSerializer.Serialize(message),
                    OccurredUtc = message.OccurredUtc,
                    AttemptCount = 0
                });
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(staffUserId, "DocumentVerified", documentId, document.OrganizationId, notes, cancellationToken);
    }

    public async Task RejectVerificationAsync(Guid staffUserId, Guid documentId, string reason, CancellationToken cancellationToken = default)
    {
        StoredDocument document = await dbContext.Documents.SingleAsync(item => item.Id == documentId, cancellationToken);
        document.RejectVerification(staffUserId, clock.UtcNow, reason);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(staffUserId, "DocumentVerificationRejected", documentId, document.OrganizationId, reason, cancellationToken);
    }

    public async Task SetRetentionAsync(Guid staffUserId, Guid documentId, DocumentRetentionClass retentionClass, DateTimeOffset? retainUntilUtc, CancellationToken cancellationToken = default)
    {
        StoredDocument document = await dbContext.Documents.SingleAsync(item => item.Id == documentId, cancellationToken);
        document.SetRetention(retentionClass, retainUntilUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(staffUserId, "DocumentRetentionChanged", documentId, document.OrganizationId, $"Retention={retentionClass}; retainUntil={retainUntilUtc:O}", cancellationToken);
    }

    public async Task ArchiveAsync(Guid staffUserId, Guid documentId, CancellationToken cancellationToken = default)
    {
        StoredDocument document = await dbContext.Documents.SingleAsync(item => item.Id == documentId, cancellationToken);
        document.MarkArchived(clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(staffUserId, "DocumentArchived", documentId, document.OrganizationId, null, cancellationToken);
    }

    private async Task<IReadOnlyList<DocumentSummary>> QueryDocumentsAsync(DocumentOwnerType ownerType, Guid ownerId, CancellationToken cancellationToken)
    {
        return await dbContext.Documents.AsNoTracking().Where(document => document.OwnerType == ownerType && document.OwnerId == ownerId).OrderByDescending(document => document.UploadedUtc).Select(document => new DocumentSummary(document.Id, document.DocumentType, document.OriginalFileName, document.ContentType, document.SizeBytes, document.UploadedUtc, document.VerificationStatus, document.RetentionClass, document.RetainUntilUtc, document.IsArchived, document.VerificationNotes)).ToListAsync(cancellationToken);
    }

    private async Task<Guid> StoreMetadataAsync(Guid userId, DocumentOwnerType ownerType, Guid ownerId, Guid organizationId, DocumentType documentType, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken)
    {
        Guid documentId = Guid.NewGuid();
        string safeFileName = Path.GetFileName(originalFileName);
        string storageKey = $"organizations/{organizationId:N}/{ownerType}/{ownerId:N}/{documentId:N}-{safeFileName}";
        DateTimeOffset now = clock.UtcNow;
        StoredFile stored = await fileStorage.StoreAsync(
            new StorageWriteRequest(
                storageKey,
                content,
                contentType,
                safeFileName,
                userId.ToString("D"),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Application"] = "RequestPortal",
                    ["OrganizationId"] = organizationId.ToString("D"),
                    ["OwnerType"] = ownerType.ToString(),
                    ["OwnerId"] = ownerId.ToString("D"),
                    ["DocumentType"] = documentType.ToString()
                }),
            cancellationToken);
        StoredDocument document = new(documentId, ownerType, ownerId, organizationId, documentType, safeFileName, stored.ContentType, stored.Length, stored.StorageKey, userId, now);
        dbContext.Documents.Add(document);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return documentId;
        }
        catch
        {
            dbContext.Entry(document).State = EntityState.Detached;
            await RollBackStorageAsync(stored.StorageKey, cancellationToken);
            throw;
        }
    }

    private async Task RollBackStorageAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (fileStorage is IStorageLifecycle lifecycle)
        {
            await lifecycle.PurgeAsync(storageKey, cancellationToken);
            return;
        }

        await fileStorage.DeleteAsync(storageKey, cancellationToken);
    }

    private Task AuditAsync(Guid actorUserId, string action, Guid documentId, Guid organizationId, string? detail, CancellationToken cancellationToken)
    {
        return auditWriter.WriteAsync(actorUserId, action, "Document", documentId, organizationId, detail, cancellationToken);
    }

    private async Task EnsureApprovedMemberAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken)
    {
        bool allowed = await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken);
        if (!allowed) throw new UnauthorizedAccessException("The current user cannot access documents for this organization.");
    }

    private static Guid CreateDeterministicGuid(Guid id, string purpose)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{id:D}|{purpose}"));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
