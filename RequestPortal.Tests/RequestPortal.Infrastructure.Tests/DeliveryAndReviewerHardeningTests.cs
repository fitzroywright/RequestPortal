using System.Security.Cryptography;
using System.Text;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Delivery;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Reviewers;
using RequestPortal.Domain.Delivery;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Delivery;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class DeliveryAndReviewerHardeningTests
{
    [Fact]
    public void ReviewerScopes_AcceptSupportedScopes_AndRejectMalformedScopes()
    {
        Guid organizationId = Guid.NewGuid();
        Guid requestId = Guid.NewGuid();

        Assert.True(ExternalReviewerScopes.IsSupported(ExternalReviewerScopes.ManagementDashboard));
        Assert.True(ExternalReviewerScopes.IsSupported(ExternalReviewerScopes.DeliverySummary));
        Assert.True(ExternalReviewerScopes.IsSupported(ExternalReviewerScopes.OrganizationPrefix + organizationId));
        Assert.True(ExternalReviewerScopes.IsSupported(ExternalReviewerScopes.RequestPrefix + requestId));
        Assert.True(ExternalReviewerScopes.IsSupported(
            ExternalReviewerScopes.DateRangePrefix +
            "2026-09-01T00:00:00.0000000+00:00|2026-09-30T23:59:59.0000000+00:00"));

        Assert.False(ExternalReviewerScopes.IsSupported("Organization:not-a-guid"));
        Assert.False(ExternalReviewerScopes.IsSupported("Request:"));
        Assert.False(ExternalReviewerScopes.IsSupported("DateRange:bad|range"));
        Assert.False(ExternalReviewerScopes.IsSupported("UnknownScope"));
    }

    [Fact]
    public async Task DeliveryEvidence_RejectsInvalidToken()
    {
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync();
        Guid requestId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        AssistanceRequest request = new(requestId, "RP-TEST-1", organizationId, userId, DateTimeOffset.UtcNow);
        database.Db.AssistanceRequests.Add(request);

        string validToken = "valid-token";
        DeliveryRecord delivery = new(
            Guid.NewGuid(),
            requestId,
            "Delivery Note",
            "DN-1",
            "{\"Lines\":[]}",
            Hash(validToken),
            "Recipient",
            "Driver",
            DateTimeOffset.UtcNow);
        database.Db.Deliveries.Add(delivery);
        await database.Db.SaveChangesAsync();

        FakeDocumentService documents = new();
        DeliveryService service = new(database.Db, new FakeAuditWriter(), documents);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UploadEvidenceAsync(
                delivery.Id,
                "wrong-token",
                "photo.jpg",
                "image/jpeg",
                3,
                new MemoryStream([1, 2, 3])));

        Assert.False(documents.UploadCalled);
    }

    [Fact]
    public async Task DeliveryEvidence_UsesRequestScopedDocumentStorageForValidToken()
    {
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync();
        Guid requestId = Guid.NewGuid();
        Guid organizationId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        AssistanceRequest request = new(requestId, "RP-TEST-2", organizationId, userId, DateTimeOffset.UtcNow);
        database.Db.AssistanceRequests.Add(request);

        string token = "secure-token";
        DeliveryRecord delivery = new(
            Guid.NewGuid(),
            requestId,
            "Delivery Note",
            "DN-2",
            "{\"Lines\":[{\"Description\":\"Rice\",\"Quantity\":10,\"UnitOfMeasure\":\"bag\"}]}",
            Hash(token),
            "Recipient",
            "Driver",
            DateTimeOffset.UtcNow);
        database.Db.Deliveries.Add(delivery);
        await database.Db.SaveChangesAsync();

        FakeDocumentService documents = new();
        DeliveryService service = new(database.Db, new FakeAuditWriter(), documents);

        Guid documentId = await service.UploadEvidenceAsync(
            delivery.Id,
            token,
            "photo.jpg",
            "image/jpeg",
            3,
            new MemoryStream([1, 2, 3]));

        Assert.True(documents.UploadCalled);
        Assert.Equal(requestId, documents.RequestId);
        Assert.Equal(documents.DocumentId, documentId);
    }

    [Fact]
    public async Task DeliveryView_ProjectsImmutableLineItemsForDisplay()
    {
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync();
        Guid requestId = Guid.NewGuid();
        AssistanceRequest request = new(requestId, "RP-TEST-3", Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        database.Db.AssistanceRequests.Add(request);

        DeliveryRecord delivery = new(
            Guid.NewGuid(),
            requestId,
            "Delivery Note",
            "DN-3",
            "{\"Lines\":[{\"Description\":\"School bag\",\"Quantity\":2,\"UnitOfMeasure\":\"each\"}]}",
            Hash("token"),
            "Recipient",
            null,
            DateTimeOffset.UtcNow);
        database.Db.Deliveries.Add(delivery);
        await database.Db.SaveChangesAsync();

        DeliveryService service = new(database.Db, new FakeAuditWriter(), new FakeDocumentService());
        IReadOnlyList<DeliveryView> views = await service.GetForRequestAsync(requestId);

        DeliveryView view = Assert.Single(views);
        DeliveryLineSnapshot line = Assert.Single(view.Lines);
        Assert.Equal("School bag", line.Description);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal("each", line.UnitOfMeasure);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class FakeAuditWriter : IBusinessAuditWriter
    {
        public Task WriteAsync(
            Guid actorUserId,
            string action,
            string entityType,
            Guid entityId,
            Guid? organizationId,
            string? detail = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeDocumentService : IDocumentService
    {
        public Guid DocumentId { get; } = Guid.NewGuid();
        public bool UploadCalled { get; private set; }
        public Guid RequestId { get; private set; }

        public Task<Guid> UploadDeliveryEvidenceAsync(
            Guid requestId,
            string originalFileName,
            string contentType,
            long sizeBytes,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            UploadCalled = true;
            RequestId = requestId;
            return Task.FromResult(DocumentId);
        }

        public Task<Guid> UploadForRequestAsync(Guid userId, Guid requestId, DocumentType documentType, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid> UploadForOrganizationAsync(Guid userId, Guid organizationId, DocumentType documentType, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentSummary>> GetRequestDocumentsAsync(Guid userId, Guid requestId, bool isStaff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DocumentSummary>> GetOrganizationDocumentsAsync(Guid userId, Guid organizationId, bool isStaff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DocumentDownload?> DownloadAsync(Guid userId, Guid documentId, bool isStaff, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task VerifyAsync(Guid staffUserId, Guid documentId, string? notes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RejectVerificationAsync(Guid staffUserId, Guid documentId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetRetentionAsync(Guid staffUserId, Guid documentId, DocumentRetentionClass retentionClass, DateTimeOffset? retainUntilUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ArchiveAsync(Guid staffUserId, Guid documentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
