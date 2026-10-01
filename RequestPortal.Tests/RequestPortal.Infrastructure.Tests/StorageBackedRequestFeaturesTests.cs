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
                storage,
                NullLogger<ProjectProposalService>.Instance);

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
    public async Task RequestLocation_RoundTripsThroughCommonStorage()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = CreateTemporaryDirectory();
        try
        {
            LocalFileStorage storage = new(root);
            RequestLocationService service = new(storage);
            Guid requestId = Guid.NewGuid();
            const string sourceText = "Half Way Tree, St. Andrew, Jamaica";

            RequestLocationSnapshot saved = await service.ResolveAndSaveAsync(
                requestId,
                sourceText,
                latitude: null,
                longitude: null,
                cancellationToken)
                ?? throw new InvalidOperationException("Location was not resolved.");

            Assert.Equal(requestId, saved.RequestId);
            Assert.Equal(sourceText, saved.SourceText);
            Assert.Equal("St. Andrew", saved.Parish);
            Assert.Equal("Half Way Tree", saved.Community);

            string key = $"requestportal/locations/{requestId:N}/resolved-location.json";
            StoredFile? metadata = await storage.GetMetadataAsync(key, cancellationToken);
            Assert.NotNull(metadata);
            Assert.Equal(StorageStatus.Stored, metadata!.Status);
            Assert.Equal("application/json", metadata.ContentType);

            await using Stream stream = await storage.OpenReadAsync(key, cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            JsonElement rootElement = document.RootElement;
            Assert.Equal(requestId, rootElement.GetProperty("requestId").GetGuid());
            Assert.Equal(sourceText, rootElement.GetProperty("sourceText").GetString());
            Assert.Equal("St. Andrew", rootElement.GetProperty("parish").GetString());
            Assert.Equal("Half Way Tree", rootElement.GetProperty("community").GetString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
