using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Domain.Documents;

namespace RequestPortal.Application.Documents;

public sealed record DocumentSummary(
    Guid Id,
    DocumentType DocumentType,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset UploadedUtc,
    DocumentVerificationStatus VerificationStatus,
    DocumentRetentionClass RetentionClass,
    DateTimeOffset? RetainUntilUtc,
    bool IsArchived,
    string? VerificationNotes);

public sealed record DocumentDownload(Stream Content, string ContentType, string FileName);

public interface IDocumentPreviewRenderer
{
    Task<string?> RenderHtmlAsync(
        string fileName,
        string contentType,
        Stream content,
        CancellationToken cancellationToken = default);
}

public interface IDocumentService
{
    Task<Guid> UploadForRequestAsync(
        Guid userId,
        Guid requestId,
        DocumentType documentType,
        string originalFileName,
        string contentType,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<Guid> UploadDeliveryEvidenceAsync(
        Guid requestId,
        Guid deliveryId,
        string originalFileName,
        string contentType,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<Guid> UploadForOrganizationAsync(
        Guid userId,
        Guid organizationId,
        DocumentType documentType,
        string originalFileName,
        string contentType,
        long sizeBytes,
        Stream content,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentSummary>> GetRequestDocumentsAsync(
        Guid userId,
        Guid requestId,
        bool isStaff,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentSummary>> GetOrganizationDocumentsAsync(
        Guid userId,
        Guid organizationId,
        bool isStaff,
        CancellationToken cancellationToken = default);

    Task<DocumentDownload?> DownloadAsync(
        Guid userId,
        Guid documentId,
        bool isStaff,
        CancellationToken cancellationToken = default);

    Task VerifyAsync(
        Guid staffUserId,
        Guid documentId,
        string? notes,
        CancellationToken cancellationToken = default);

    Task RejectVerificationAsync(
        Guid staffUserId,
        Guid documentId,
        string reason,
        CancellationToken cancellationToken = default);

    Task SetRetentionAsync(
        Guid staffUserId,
        Guid documentId,
        DocumentRetentionClass retentionClass,
        DateTimeOffset? retainUntilUtc,
        CancellationToken cancellationToken = default);

    Task ArchiveAsync(
        Guid staffUserId,
        Guid documentId,
        CancellationToken cancellationToken = default);
}
