using System;

namespace RequestPortal.Domain.Documents;

public enum DocumentOwnerType
{
    Organization = 0,
    AssistanceRequest = 1
}

public enum DocumentType
{
    Other = 0,
    OrganizationRegistrationCertificate = 1,
    Identification = 2,
    BeneficiarySupport = 3,
    RequestSupport = 4,
    FfpCorrespondence = 5
}

public sealed class StoredDocument
{
    private StoredDocument()
    {
    }

    public StoredDocument(
        Guid id,
        DocumentOwnerType ownerType,
        Guid ownerId,
        Guid organizationId,
        DocumentType documentType,
        string originalFileName,
        string contentType,
        long sizeBytes,
        string storageKey,
        Guid uploadedByUserId,
        DateTimeOffset uploadedUtc)
    {
        if (sizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }

        Id = id;
        OwnerType = ownerType;
        OwnerId = ownerId;
        OrganizationId = organizationId;
        DocumentType = documentType;
        OriginalFileName = originalFileName.Trim();
        ContentType = contentType.Trim();
        SizeBytes = sizeBytes;
        StorageKey = storageKey.Trim();
        UploadedByUserId = uploadedByUserId;
        UploadedUtc = uploadedUtc;
    }

    public Guid Id { get; private set; }

    public DocumentOwnerType OwnerType { get; private set; }

    public Guid OwnerId { get; private set; }

    public Guid OrganizationId { get; private set; }

    public DocumentType DocumentType { get; private set; }

    public string OriginalFileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long SizeBytes { get; private set; }

    public string StorageKey { get; private set; } = string.Empty;

    public Guid UploadedByUserId { get; private set; }

    public DateTimeOffset UploadedUtc { get; private set; }

    public DateTimeOffset? RetainUntilUtc { get; private set; }

    public bool IsArchived { get; private set; }

    public void SetRetention(DateTimeOffset retainUntilUtc)
    {
        RetainUntilUtc = retainUntilUtc;
    }

    public void MarkArchived()
    {
        IsArchived = true;
    }
}
