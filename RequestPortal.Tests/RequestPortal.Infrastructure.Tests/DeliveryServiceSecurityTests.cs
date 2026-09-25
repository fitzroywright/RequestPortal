using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Delivery;
using RequestPortal.Application.Documents;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Delivery;
using RequestPortal.Infrastructure.Identity;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class DeliveryServiceSecurityTests
{
    [Fact]
    public async Task DeliveryTokenProtectsViewAndEvidenceUpload()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ApplicationUser user = new()
        {
            Id = Guid.NewGuid(),
            UserName = "owner@example.org",
            NormalizedUserName = "OWNER@EXAMPLE.ORG",
            Email = "owner@example.org",
            NormalizedEmail = "OWNER@EXAMPLE.ORG",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            DisplayName = "Owner",
            CreatedUtc = now
        };
        database.Db.Users.Add(user);

        Organization organization = new(
            Guid.NewGuid(),
            "Delivery Test Organization",
            "JM",
            user.Id,
            now);
        organization.RequestMembership(Guid.NewGuid(), user.Id, now);
        organization.Approve(Guid.NewGuid(), now.AddMinutes(1));
        database.Db.Organizations.Add(organization);

        AssistanceRequest request = new(
            Guid.NewGuid(),
            "RP-POD-001",
            organization.Id,
            user.Id,
            now);
        request.SetSummary("POD security test");
        database.Db.AssistanceRequests.Add(request);
        await database.Db.SaveChangesAsync(cancellationToken);

        StubDocumentService documents = new();
        DeliveryService service = new(database.Db, new NullAuditWriter(), documents);

        CreateDeliveryResult created = await service.CreateAsync(
            new CreateDeliveryCommand(
                request.Id,
                "Delivery Note",
                "DN-100",
                "Recipient",
                "Driver",
                [new DeliveryLineSnapshot("Rice", 10m, "bags")],
                null),
            cancellationToken);

        Assert.NotNull(await service.GetAsync(created.DeliveryId, created.AccessToken, cancellationToken));
        Assert.Null(await service.GetAsync(created.DeliveryId, "BAD-TOKEN", cancellationToken));

        await using MemoryStream evidence = new([1, 2, 3, 4]);
        Guid evidenceId = await service.UploadEvidenceAsync(
            created.DeliveryId,
            created.AccessToken,
            "delivery.jpg",
            "image/jpeg",
            evidence.Length,
            evidence,
            cancellationToken);

        Assert.Equal(documents.EvidenceDocumentId, evidenceId);

        await using MemoryStream rejectedEvidence = new([1]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UploadEvidenceAsync(
                created.DeliveryId,
                "BAD-TOKEN",
                "delivery.jpg",
                "image/jpeg",
                rejectedEvidence.Length,
                rejectedEvidence,
                cancellationToken));
    }

    [Fact]
    public async Task RequestDeliveryListReflectsAcknowledgementOutcome()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ApplicationUser user = new()
        {
            Id = Guid.NewGuid(),
            UserName = "owner2@example.org",
            NormalizedUserName = "OWNER2@EXAMPLE.ORG",
            Email = "owner2@example.org",
            NormalizedEmail = "OWNER2@EXAMPLE.ORG",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            DisplayName = "Owner",
            CreatedUtc = now
        };
        database.Db.Users.Add(user);

        Organization organization = new(Guid.NewGuid(), "POD Outcome Organization", "JM", user.Id, now);
        organization.RequestMembership(Guid.NewGuid(), user.Id, now);
        organization.Approve(Guid.NewGuid(), now.AddMinutes(1));
        database.Db.Organizations.Add(organization);

        AssistanceRequest request = new(Guid.NewGuid(), "RP-POD-002", organization.Id, user.Id, now);
        request.SetSummary("POD outcome test");
        database.Db.AssistanceRequests.Add(request);
        await database.Db.SaveChangesAsync(cancellationToken);

        DeliveryService service = new(database.Db, new NullAuditWriter(), new StubDocumentService());
        CreateDeliveryResult created = await service.CreateAsync(
            new CreateDeliveryCommand(
                request.Id,
                "Delivery Note",
                "DN-101",
                "Recipient",
                null,
                [new DeliveryLineSnapshot("Water", 5m, "cases")],
                null),
            cancellationToken);

        await service.AcknowledgeAsync(
            created.DeliveryId,
            created.AccessToken,
            "PartiallyDelivered",
            "Recipient",
            "Recipient",
            null,
            "One case damaged.",
            cancellationToken);

        DeliveryView delivery = Assert.Single(await service.GetForRequestAsync(request.Id, cancellationToken));
        Assert.Equal("PartiallyDelivered", delivery.Status);
        Assert.Equal("Recipient", delivery.AcknowledgedBy);
        Assert.Equal("One case damaged.", delivery.Notes);
    }

    private sealed class NullAuditWriter : IBusinessAuditWriter
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

    private sealed class StubDocumentService : IDocumentService
    {
        public Guid EvidenceDocumentId { get; } = Guid.NewGuid();

        public Task<Guid> UploadDeliveryEvidenceAsync(
            Guid requestId,
            Guid deliveryId,
            string originalFileName,
            string contentType,
            long sizeBytes,
            Stream content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EvidenceDocumentId);

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
