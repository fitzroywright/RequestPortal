using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Security;
using RequestPortal.Common.Storage;
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

    public DocumentService(
        RequestPortalDbContext dbContext,
        IOrganizationAccessService organizationAccessService,
        IFileStorage fileStorage,
        IClock clock)
    {
        this.dbContext = dbContext;
        this.organizationAccessService = organizationAccessService;
        this.fileStorage = fileStorage;
        this.clock = clock;
    }

    public async Task<Guid> UploadForRequestAsync(
        Guid userId,
        Guid requestId,
        DocumentType documentType,
        string originalFileName,
        string contentType,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        Guid organizationId = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Where(request => request.Id == requestId)
            .Select(request => request.OrganizationId)
            .SingleAsync(cancellationToken);

        await EnsureApprovedMemberAsync(userId, organizationId, cancellationToken);

        return await StoreMetadataAsync(
            userId,
            DocumentOwnerType.AssistanceRequest,
            requestId,
            organizationId,
            documentType,
            originalFileName,
            contentType,
            sizeBytes,
            content,
            cancellationToken);
    }

    public async Task<Guid> UploadForOrganizationAsync(
        Guid userId,
        Guid organizationId,
        DocumentType documentType,
        string originalFileName,
        string contentType,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        Organization organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleAsync(item => item.Id == organizationId, cancellationToken);

        bool isApprovedMember = await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken);
        if (organization.RegisteredByUserId != userId && !isApprovedMember)
        {
            throw new UnauthorizedAccessException("The current user cannot upload documents for this organization.");
        }

        return await StoreMetadataAsync(
            userId,
            DocumentOwnerType.Organization,
            organizationId,
            organizationId,
            documentType,
            originalFileName,
            contentType,
            sizeBytes,
            content,
            cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentSummary>> GetRequestDocumentsAsync(
        Guid userId,
        Guid requestId,
        bool isStaff,
        CancellationToken cancellationToken = default)
    {
        Guid organizationId = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Where(request => request.Id == requestId)
            .Select(request => request.OrganizationId)
            .SingleAsync(cancellationToken);

        if (!isStaff)
        {
            await EnsureApprovedMemberAsync(userId, organizationId, cancellationToken);
        }

        return await QueryDocumentsAsync(DocumentOwnerType.AssistanceRequest, requestId, cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentSummary>> GetOrganizationDocumentsAsync(
        Guid userId,
        Guid organizationId,
        bool isStaff,
        CancellationToken cancellationToken = default)
    {
        if (!isStaff)
        {
            Organization organization = await dbContext.Organizations
                .AsNoTracking()
                .SingleAsync(item => item.Id == organizationId, cancellationToken);

            bool isApprovedMember = await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken);
            if (organization.RegisteredByUserId != userId && !isApprovedMember)
            {
                throw new UnauthorizedAccessException("The current user cannot access documents for this organization.");
            }
        }

        return await QueryDocumentsAsync(DocumentOwnerType.Organization, organizationId, cancellationToken);
    }

    public async Task<DocumentDownload?> DownloadAsync(
        Guid userId,
        Guid documentId,
        bool isStaff,
        CancellationToken cancellationToken = default)
    {
        StoredDocument? document = await dbContext.Documents
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == documentId, cancellationToken);

        if (document is null)
        {
            return null;
        }

        if (!isStaff)
        {
            await EnsureApprovedMemberAsync(userId, document.OrganizationId, cancellationToken);
        }

        Stream stream = await fileStorage.OpenReadAsync(document.StorageKey, cancellationToken);
        return new DocumentDownload(stream, document.ContentType, document.OriginalFileName);
    }

    public async Task VerifyAsync(
        Guid staffUserId,
        Guid documentId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        StoredDocument document = await dbContext.Documents
            .SingleAsync(item => item.Id == documentId, cancellationToken);

        document.Verify(staffUserId, clock.UtcNow, notes);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RejectVerificationAsync(
        Guid staffUserId,
        Guid documentId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        StoredDocument document = await dbContext.Documents
            .SingleAsync(item => item.Id == documentId, cancellationToken);

        document.RejectVerification(staffUserId, clock.UtcNow, reason);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetRetentionAsync(
        Guid staffUserId,
        Guid documentId,
        DocumentRetentionClass retentionClass,
        DateTimeOffset? retainUntilUtc,
        CancellationToken cancellationToken = default)
    {
        _ = staffUserId;
        StoredDocument document = await dbContext.Documents
            .SingleAsync(item => item.Id == documentId, cancellationToken);

        document.SetRetention(retentionClass, retainUntilUtc);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(
        Guid staffUserId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        _ = staffUserId;
        StoredDocument document = await dbContext.Documents
            .SingleAsync(item => item.Id == documentId, cancellationToken);

        document.MarkArchived(clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<DocumentSummary>> QueryDocumentsAsync(
        DocumentOwnerType ownerType,
        Guid ownerId,
        CancellationToken cancellationToken)
    {
        List<DocumentSummary> documents = await dbContext.Documents
            .AsNoTracking()
            .Where(document => document.OwnerType == ownerType && document.OwnerId == ownerId)
            .OrderByDescending(document => document.UploadedUtc)
            .Select(document => new DocumentSummary(
                document.Id,
                document.DocumentType,
                document.OriginalFileName,
                document.ContentType,
                document.SizeBytes,
                document.UploadedUtc,
                document.VerificationStatus,
                document.RetentionClass,
                document.RetainUntilUtc,
                document.IsArchived,
                document.VerificationNotes))
            .ToListAsync(cancellationToken);

        return documents;
    }

    private async Task<Guid> StoreMetadataAsync(
        Guid userId,
        DocumentOwnerType ownerType,
        Guid ownerId,
        Guid organizationId,
        DocumentType documentType,
        string originalFileName,
        string contentType,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken)
    {
        Guid documentId = Guid.NewGuid();
        string safeFileName = Path.GetFileName(originalFileName);
        string storageKey = $"organizations/{organizationId:N}/{ownerType}/{ownerId:N}/{documentId:N}-{safeFileName}";
        DateTimeOffset now = clock.UtcNow;

        StoredFile stored = await fileStorage.StoreAsync(storageKey, content, contentType, cancellationToken);

        StoredDocument document = new(
            documentId,
            ownerType,
            ownerId,
            organizationId,
            documentType,
            safeFileName,
            stored.ContentType,
            sizeBytes,
            stored.StorageKey,
            userId,
            now);

        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync(cancellationToken);
        return documentId;
    }

    private async Task EnsureApprovedMemberAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken)
    {
        bool allowed = await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken);
        if (!allowed)
        {
            throw new UnauthorizedAccessException("The current user cannot access documents for this organization.");
        }
    }
}
