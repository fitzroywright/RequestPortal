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
    bool IsArchived);

public sealed record DocumentDownload(Stream Content, string ContentType, string FileName);

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

    Task<DocumentDownload?> DownloadAsync(
        Guid userId,
        Guid documentId,
        bool isStaff,
        CancellationToken cancellationToken = default);
}
