using System.Text.Json;
using Common.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Management;
using RequestPortal.Application.Requests;
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
            await using PostgreSqlTestDatabase database =
                await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
            RequestPortalDbContext db = database.Db;
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
                NullLogger<ProjectProposalService>.Instance,
                storage);

            await service.InitializeProjectAsync(userId, requestId, cancellationToken);
            await service.SaveAsync(
                userId,
                requestId,
                "Revised Proposal",
                "<h1>Safe heading</h1><p>Safe body</p><script>alert('no')</script>",
                cancellationToken);

            ProjectProposalDetail proposal = await service.GetAsync(userId, requestId, isStaff: false, cancellationToken)
                ?? throw new InvalidOperationException("Proposal was not returned after save.");
            Assert.Equal("Revised Proposal", proposal.Title);
            Assert.Contains("Safe heading", proposal.RichContentHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", proposal.RichContentHtml, StringComparison.OrdinalIgnoreCase);

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
    public async Task RequestLocation_RoundTripsThroughPostgresAndQueuesDurableRefresh()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);
        var actor=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        var org=new RequestPortal.Domain.Organizations.Organization(Guid.NewGuid(),"Location fixture","JM",actor,now);
        var request=new AssistanceRequest(Guid.NewGuid(),"LOC-1",org.Id,actor,now);
        fixture.Db.Organizations.Add(org);fixture.Db.AssistanceRequests.Add(request);await fixture.Db.SaveChangesAsync(ct);
        await fixture.Db.OutboxMessages.ExecuteDeleteAsync(ct);
        var service=new RequestLocationService(fixture.Db);
        var saved=await service.ResolveAndSaveAsync(request.Id,"Half Way Tree, St. Andrew, Jamaica",null,null,ct);
        Assert.NotNull(saved);Assert.Equal("St. Andrew",saved.Parish);Assert.Equal("Half Way Tree",saved.Community);
        await using var read=fixture.CreateSiblingContext();
        var row=await read.RequestLocations.SingleAsync(ct);Assert.Equal(saved,row.Snapshot());
        Assert.True(await read.OutboxMessages.AnyAsync(x=>x.MessageType==ManagementDashboardAggregateRefresher.MessageType && x.ProcessedUtc==null,ct));
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "requestportal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class AllowAllOrganizationAccess : IOrganizationAccessService
    {
        public Task<bool> IsApprovedMemberAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> IsAdministratorAsync(Guid userId, Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
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
