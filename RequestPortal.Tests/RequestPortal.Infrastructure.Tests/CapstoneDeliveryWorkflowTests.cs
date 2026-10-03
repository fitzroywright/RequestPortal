using System.Text.Json;
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

public sealed class CapstoneDeliveryWorkflowTests
{
    [Fact]
    public async Task DeliveryOrderLinkage_IsDeterministic()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        TestContextData data = await CreateContextAsync(database, cancellationToken);
        DeliveryService service = CreateService(database);

        CreateDeliveryCommand command = DeliveryCommand(
            data.Request.Id,
            "DO-FFP-1001",
            returnsPermitted: false);

        CreateDeliveryResult first = await service.CreateAsync(command, cancellationToken);
        CreateDeliveryResult second = await service.CreateAsync(command, cancellationToken);

        Assert.Equal(first.DeliveryId, second.DeliveryId);
        DeliveryView delivery = Assert.Single(await service.GetForRecipientRequestAsync(
            data.Owner.Id,
            data.Request.Id,
            cancellationToken));
        Assert.Equal("DO-FFP-1001", delivery.FfpDeliveryOrderId);
    }

    [Fact]
    public async Task RecipientConfirmation_PreservesIndependentLineQuantitiesAndAuditIdentity()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        TestContextData data = await CreateContextAsync(database, cancellationToken);
        DeliveryService service = CreateService(database);
        CreateDeliveryResult created = await service.CreateAsync(
            DeliveryCommand(data.Request.Id, "DO-1002", false),
            cancellationToken);

        await service.ConfirmReceiptAsync(
            data.Owner.Id,
            data.Request.Id,
            created.DeliveryId,
            [
                new RecipientConfirmedLine("Rice", 10m, 10m, "bags"),
                new RecipientConfirmedLine("Water", 5m, 5m, "cases")
            ],
            Array.Empty<DeliveryDiscrepancy>(),
            cancellationToken);

        RecipientReceiptConfirmation confirmation = Assert.Single(
            await service.GetUnsyncedRecipientConfirmationsAsync(cancellationToken));
        Assert.Equal(data.Owner.Id, confirmation.ConfirmedByUserId);
        Assert.Equal("DO-1002", confirmation.FfpDeliveryOrderId);
        Assert.Equal(2, confirmation.Items.Count);
        Assert.Empty(confirmation.Discrepancies);
    }

    [Fact]
    public async Task PartialReceipt_ProducesRecipientDiscrepancyPayload()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        TestContextData data = await CreateContextAsync(database, cancellationToken);
        DeliveryService service = CreateService(database);
        CreateDeliveryResult created = await service.CreateAsync(
            DeliveryCommand(data.Request.Id, "DO-1003", false),
            cancellationToken);

        await service.ConfirmReceiptAsync(
            data.Owner.Id,
            data.Request.Id,
            created.DeliveryId,
            [
                new RecipientConfirmedLine("Rice", 10m, 8m, "bags"),
                new RecipientConfirmedLine("Water", 5m, 5m, "cases")
            ],
            [
                new DeliveryDiscrepancy(
                    "IncorrectQuantity",
                    "Rice",
                    10m,
                    8m,
                    "Two bags were missing.")
            ],
            cancellationToken);

        RecipientReceiptConfirmation confirmation = Assert.Single(
            await service.GetUnsyncedRecipientConfirmationsAsync(cancellationToken));
        DeliveryDiscrepancy discrepancy = Assert.Single(confirmation.Discrepancies);
        Assert.Equal("Rice", discrepancy.ItemDescription);
        Assert.Equal(8m, discrepancy.ReceivedQuantity);
    }

    [Fact]
    public async Task DuplicateConfirmation_IsRejected()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        TestContextData data = await CreateContextAsync(database, cancellationToken);
        DeliveryService service = CreateService(database);
        CreateDeliveryResult created = await service.CreateAsync(
            DeliveryCommand(data.Request.Id, "DO-1004", false),
            cancellationToken);
        RecipientConfirmedLine[] lines =
        [
            new("Rice", 10m, 10m, "bags"),
            new("Water", 5m, 5m, "cases")
        ];

        await service.ConfirmReceiptAsync(
            data.Owner.Id,
            data.Request.Id,
            created.DeliveryId,
            lines,
            Array.Empty<DeliveryDiscrepancy>(),
            cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmReceiptAsync(
                data.Owner.Id,
                data.Request.Id,
                created.DeliveryId,
                lines,
                Array.Empty<DeliveryDiscrepancy>(),
                cancellationToken));
    }

    [Fact]
    public async Task ApplicantIsolation_RejectsUnrelatedUser()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        TestContextData data = await CreateContextAsync(database, cancellationToken);
        DeliveryService service = CreateService(database);
        CreateDeliveryResult created = await service.CreateAsync(
            DeliveryCommand(data.Request.Id, "DO-1005", false),
            cancellationToken);
        Guid unrelatedUser = Guid.NewGuid();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ConfirmReceiptAsync(
                unrelatedUser,
                data.Request.Id,
                created.DeliveryId,
                [
                    new RecipientConfirmedLine("Rice", 10m, 10m, "bags"),
                    new RecipientConfirmedLine("Water", 5m, 5m, "cases")
                ],
                Array.Empty<DeliveryDiscrepancy>(),
                cancellationToken));
    }

    [Fact]
    public async Task ReturnInitiation_RequiresFfpPermissionAndConfirmedReceipt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        TestContextData data = await CreateContextAsync(database, cancellationToken);
        DeliveryService service = CreateService(database);

        CreateDeliveryResult blocked = await service.CreateAsync(
            DeliveryCommand(data.Request.Id, "DO-1006", false),
            cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InitiateReturnAsync(
                data.Owner.Id,
                data.Request.Id,
                blocked.DeliveryId,
                "Rice",
                1m,
                "Not required",
                cancellationToken));

        CreateDeliveryResult permitted = await service.CreateAsync(
            DeliveryCommand(data.Request.Id, "DO-1007", true),
            cancellationToken);

        await service.ConfirmReceiptAsync(
            data.Owner.Id,
            data.Request.Id,
            permitted.DeliveryId,
            [
                new RecipientConfirmedLine("Rice", 10m, 10m, "bags"),
                new RecipientConfirmedLine("Water", 5m, 5m, "cases")
            ],
            Array.Empty<DeliveryDiscrepancy>(),
            cancellationToken);

        DeliveryReturnView returnRequest = await service.InitiateReturnAsync(
            data.Owner.Id,
            data.Request.Id,
            permitted.DeliveryId,
            "Rice",
            2m,
            "Two bags are damaged.",
            cancellationToken);

        Assert.Equal("Requested", returnRequest.Status);
        Assert.Equal(2m, returnRequest.Quantity);
    }

    [Fact]
    public async Task ReturnStatusSynchronization_UsesFfpManagerAuthoritativeState()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        TestContextData data = await CreateContextAsync(database, cancellationToken);
        DeliveryService service = CreateService(database);
        CreateDeliveryResult delivery = await service.CreateAsync(
            DeliveryCommand(data.Request.Id, "DO-1008", true),
            cancellationToken);

        await service.ConfirmReceiptAsync(
            data.Owner.Id,
            data.Request.Id,
            delivery.DeliveryId,
            [
                new RecipientConfirmedLine("Rice", 10m, 10m, "bags"),
                new RecipientConfirmedLine("Water", 5m, 5m, "cases")
            ],
            Array.Empty<DeliveryDiscrepancy>(),
            cancellationToken);

        DeliveryReturnView requested = await service.InitiateReturnAsync(
            data.Owner.Id,
            data.Request.Id,
            delivery.DeliveryId,
            "Rice",
            1m,
            "Damaged",
            cancellationToken);

        await service.MarkReturnSyncedAsync(requested.Id, "RET-77", cancellationToken);
        await service.ApplyReturnStatusAsync(
            requested.Id,
            "Completed",
            "Return received by FFP.",
            DateTimeOffset.UtcNow,
            cancellationToken);

        DeliveryReturnView current = Assert.Single(
            await service.GetReturnsForRequestAsync(data.Owner.Id, data.Request.Id, cancellationToken));
        Assert.Equal("RET-77", current.FfpReturnId);
        Assert.Equal("Completed", current.Status);
        Assert.Equal("Return received by FFP.", current.StatusDetail);
    }

    [Fact]
    public async Task SynchronizationFailure_RemainsRetryableUntilExplicitAcknowledgement()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
        TestContextData data = await CreateContextAsync(database, cancellationToken);
        DeliveryService service = CreateService(database);
        CreateDeliveryResult delivery = await service.CreateAsync(
            DeliveryCommand(data.Request.Id, "DO-1009", false),
            cancellationToken);

        await service.ConfirmReceiptAsync(
            data.Owner.Id,
            data.Request.Id,
            delivery.DeliveryId,
            [
                new RecipientConfirmedLine("Rice", 10m, 10m, "bags"),
                new RecipientConfirmedLine("Water", 5m, 5m, "cases")
            ],
            Array.Empty<DeliveryDiscrepancy>(),
            cancellationToken);

        Assert.Single(await service.GetUnsyncedRecipientConfirmationsAsync(cancellationToken));
        Assert.Single(await service.GetUnsyncedRecipientConfirmationsAsync(cancellationToken));

        await service.MarkRecipientConfirmationSyncedAsync(delivery.DeliveryId, cancellationToken);

        Assert.Empty(await service.GetUnsyncedRecipientConfirmationsAsync(cancellationToken));
    }

    [Fact]
    public void CapstoneFeature_DefaultsDisabled()
    {
        CapstoneOptions options = new();
        Assert.False(options.Enabled);
    }

    private static DeliveryService CreateService(PostgreSqlTestDatabase database) =>
        new(database.Db, new NullAuditWriter(), new StubDocumentService());

    private static CreateDeliveryCommand DeliveryCommand(
        Guid requestId,
        string deliveryOrderId,
        bool returnsPermitted) =>
        new(
            requestId,
            "Delivery Order",
            deliveryOrderId,
            "Recipient",
            "Driver",
            [
                new DeliveryLineSnapshot("Rice", 10m, "bags"),
                new DeliveryLineSnapshot("Water", 5m, "cases")
            ],
            JsonSerializer.Serialize(new { deliveryOrderId }),
            FfpDeliveryOrderId: deliveryOrderId,
            ReturnsPermitted: returnsPermitted);

    private static async Task<TestContextData> CreateContextAsync(
        PostgreSqlTestDatabase database,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ApplicationUser owner = new()
        {
            Id = Guid.NewGuid(),
            UserName = "capstone@example.org",
            NormalizedUserName = "CAPSTONE@EXAMPLE.ORG",
            Email = "capstone@example.org",
            NormalizedEmail = "CAPSTONE@EXAMPLE.ORG",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            DisplayName = "Capstone Recipient",
            CreatedUtc = now
        };
        database.Db.Users.Add(owner);

        Organization organization = new(
            Guid.NewGuid(),
            "Capstone Organization",
            "JM",
            owner.Id,
            now);
        organization.RequestMembership(Guid.NewGuid(), owner.Id, now);
        organization.Approve(Guid.NewGuid(), now.AddMinutes(1));
        database.Db.Organizations.Add(organization);

        AssistanceRequest request = new(
            Guid.NewGuid(),
            "RP-CAPSTONE-001",
            organization.Id,
            owner.Id,
            now);
        request.SetSummary("Capstone delivery workflow");
        database.Db.AssistanceRequests.Add(request);
        await database.Db.SaveChangesAsync(cancellationToken);

        return new TestContextData(owner, organization, request);
    }

    private sealed record TestContextData(
        ApplicationUser Owner,
        Organization Organization,
        AssistanceRequest Request);

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
        public Task<Guid> UploadDeliveryEvidenceAsync(Guid requestId, Guid deliveryId, string originalFileName, string contentType, long sizeBytes, Stream content, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());
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
