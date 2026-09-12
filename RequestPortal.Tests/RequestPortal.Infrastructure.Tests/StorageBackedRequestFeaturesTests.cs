using Common.Storage;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Management;
using RequestPortal.Application.Security;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Management;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Requests;
using RequestPortal.Infrastructure.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StorageBackedRequestFeaturesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProjectProposal_RevisionsUseCommonStorage_AndContentIsSanitized()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTemporaryDirectory();
        try
        {
            await using RequestPortalDbContext db = CreateDb();
            Guid userId = Guid.NewGuid();
            Guid organizationId = Guid.NewGuid();
            Guid requestId = Guid.NewGuid();
            AssistanceRequest request = new(requestId, "RP-STORAGE-001", organizationId, userId, Now);
            request.SetSummary("Storage-backed proposal revision test request");
            db.AssistanceRequests.Add(request);
            await db.SaveChangesAsync(cancellationToken);

            LocalFileStorage storage = new(root);
            ProjectProposalService service = new(
                db,
                new AllowAllOrganizationAccess(),
                new FixedClock(Now),
                new NullBusinessAuditWriter(),
                storage);

            await service.InitializeProjectAsync(userId, requestId, cancellationToken);
            await service.SaveAsync(
                userId,
                requestId,
                "Revised Proposal",
                "<h1>Safe heading</h1><p>Safe body</p><script>alert('no')</script>",
                cancellationToken);

            ProjectProposalDetail proposal = Assert.NotNull(
                await service.GetAsync(userId, requestId, isStaff: false, cancellationToken));
            Assert.Equal("Revised Proposal", proposal.Title);
            Assert.Contains("Safe heading", proposal.RichContentHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", proposal.RichContentHtml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("alert('no')", proposal.RichContentHtml, StringComparison.OrdinalIgnoreCase);

            IReadOnlyList<ProjectProposalRevisionDetail> revisions = await service.GetRevisionsAsync(
                userId,
                requestId,
                isStaff: false,
                cancellationToken);
            Assert.True(revisions.Count >= 2);

            IReadOnlyList<StoredFile> storedVersions = await storage.GetVersionsAsync(
                $"requestportal/proposals/{requestId:N}/proposal-history.json",
                cancellationToken);
            Assert.True(storedVersions.Count >= 2);
            Assert.All(storedVersions, version => Assert.Equal("application/json", version.ContentType));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RequestLocation_RoundTripsThroughCommonStorage()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTemporaryDirectory();
        try
        {
            LocalFileStorage storage = new(root);
            RequestLocationService service = new(storage);
            Guid requestId = Guid.NewGuid();

            RequestLocationSnapshot? saved = await service.ResolveAndSaveAsync(
                requestId,
                "Half Way Tree, St. Andrew, Jamaica",
                null,
                null,
                cancellationToken);

            Assert.NotNull(saved);
            Assert.Equal("St. Andrew", saved.Parish);
            Assert.Equal("Half Way Tree", saved.Community);

            StoredFile? metadata = await storage.GetMetadataAsync(
                $"requestportal/locations/{requestId:N}/resolved-location.json",
                cancellationToken);
            Assert.NotNull(metadata);
            Assert.Equal(StorageStatus.Stored, metadata.Status);
            Assert.Equal("St. Andrew", metadata.Metadata["Parish"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EfModel_DoesNotContainTemporaryGeoOrProposalRevisionEntities()
    {
        using RequestPortalDbContext db = CreateDb();

        Assert.DoesNotContain(
            db.Model.GetEntityTypes(),
            entity => entity.ClrType.Name is "RequestGeoLocation" or "ProjectProposalRevision");
    }

    [Fact]
    public void ManagementDashboardPermission_UsesRequiredExactName()
    {
        Assert.Equal("ManagementDashboard.View", RequestPortalPermissions.ViewManagementDashboard);
    }

    private static RequestPortalDbContext CreateDb()
    {
        DbContextOptions<RequestPortalDbContext> options = new DbContextOptionsBuilder<RequestPortalDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new RequestPortalDbContext(options);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "requestportal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class AllowAllOrganizationAccess : IOrganizationAccessService
    {
        public Task<bool> IsApprovedMemberAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsAdministratorAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;
        public DateTimeOffset UtcNow { get; }
    }

    private sealed class NullBusinessAuditWriter : IBusinessAuditWriter
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
}
