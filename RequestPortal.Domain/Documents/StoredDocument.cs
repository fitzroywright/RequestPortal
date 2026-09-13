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

public enum DocumentVerificationStatus
{
    Pending = 0,
    Verified = 1,
    Rejected = 2
}

public enum DocumentRetentionClass
{
    Operational = 0,
    OrganizationVerification = 1,
    RequestEvidence = 2,
    LegalArchive = 3
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
        VerificationStatus = DocumentVerificationStatus.Pending;
        RetentionClass = ownerType == DocumentOwnerType.Organization
            ? DocumentRetentionClass.OrganizationVerification
            : DocumentRetentionClass.RequestEvidence;
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

    public DocumentVerificationStatus VerificationStatus { get; private set; }

    public Guid? VerifiedByUserId { get; private set; }

    public DateTimeOffset? VerifiedUtc { get; private set; }

    public string? VerificationNotes { get; private set; }

    public DocumentRetentionClass RetentionClass { get; private set; }

    public DateTimeOffset? RetainUntilUtc { get; private set; }

    public bool IsArchived { get; private set; }

    public DateTimeOffset? ArchivedUtc { get; private set; }

    public void Verify(Guid verifiedByUserId, DateTimeOffset verifiedUtc, string? notes = null)
    {
        if (IsArchived)
        {
            throw new InvalidOperationException("Archived documents cannot be verified.");
        }

        VerificationStatus = DocumentVerificationStatus.Verified;
        VerifiedByUserId = verifiedByUserId;
        VerifiedUtc = verifiedUtc;
        VerificationNotes = Normalize(notes);
    }

    public void RejectVerification(Guid verifiedByUserId, DateTimeOffset verifiedUtc, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A rejection reason is required.", nameof(reason));
        }

        VerificationStatus = DocumentVerificationStatus.Rejected;
        VerifiedByUserId = verifiedByUserId;
        VerifiedUtc = verifiedUtc;
        VerificationNotes = reason.Trim();
    }

    public void SetRetention(DocumentRetentionClass retentionClass, DateTimeOffset? retainUntilUtc)
    {
        RetentionClass = retentionClass;
        RetainUntilUtc = retainUntilUtc;
    }

    public void MarkArchived(DateTimeOffset archivedUtc)
    {
        IsArchived = true;
        ArchivedUtc = archivedUtc;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
