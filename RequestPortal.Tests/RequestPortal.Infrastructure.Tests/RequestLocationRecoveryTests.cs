using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Management;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Management;
using RequestPortal.Web.Startup;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

[CollectionDefinition("Dashboard advisory lock", DisableParallelization = true)]
public sealed class DashboardAdvisoryLockCollection { }

[Collection("Dashboard advisory lock")]
public sealed class RequestLocationRecoveryTests
{
    private static async Task<Guid> Seed(PostgreSqlTestDatabase fixture, CancellationToken ct)
    {
        var actor = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var org = new Organization(Guid.NewGuid(), "Geography fixture", "JM", actor, now);
        var request = new AssistanceRequest(Guid.NewGuid(), "GEO-1", org.Id, actor, now);
        request.SetKind(RequestKind.Project); request.SetSummary("Fixture"); request.Submit(actor, now);
        fixture.Db.Organizations.Add(org); fixture.Db.AssistanceRequests.Add(request);
        await fixture.Db.SaveChangesAsync(ct); return request.Id;
    }
    private static RequestLocationSnapshot Snapshot(Guid id) => new(id, "Half Way Tree, St. Andrew", "St. Andrew", "Half Way Tree",
        18.0179123456789m, -76.7936123456789m, LocationPrecision.Exact, DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
    private static LegacyRequestLocation Legacy(RequestLocationSnapshot snapshot)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new($"requestportal/locations/{snapshot.RequestId:N}/resolved-location.json", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
    }
    [Fact]
    public async Task VerifiedHistoricalGeographyIsPreservedInPostgresAndRepeatedImportsAreIdempotent()
    {
        var ct = TestContext.Current.CancellationToken; await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct);
        var id = await Seed(fixture, ct); var original = Snapshot(id); var source = Legacy(original);
        Assert.Equal(1, await RequestLocationRecovery.ImportAsync(fixture.Db, [source], ct));
        var saved = await fixture.Db.RequestLocations.SingleAsync(ct);
        Assert.Equal(original, saved.Snapshot()); Assert.Equal(source.ExpectedSha256, saved.ImportedSha256); Assert.Equal(source.StorageKey, saved.ImportedStorageKey);
        Assert.Equal(1, await RequestLocationRecovery.ImportAsync(fixture.Db, [source], ct));
        Assert.Equal(1, await fixture.Db.RequestLocations.CountAsync(ct));
        var marker = await fixture.Db.RequestLocationImportStates.SingleAsync(ct);
        Assert.Equal(1, marker.ImportedLocations); Assert.Equal(64, marker.SourceFingerprint.Length);
        var calculator = new ManagementDashboardService(fixture.Db, Options.Create(new ManagementDashboardOptions()));
        var snapshot = await calculator.BuildAggregateAsync(ct);
        Assert.Equal(1, snapshot.Parishes.Single(x => x.Parish == "St. Andrew").ExactLocations);
        Assert.Equal("Half Way Tree", Assert.Single(await calculator.GetRequestsAsync(new(), ct)).Community);
    }
    [Fact]
    public async Task CorruptLegacyPayloadCannotChangeLocationsOrCreateAnImportMarker()
    {
        var ct = TestContext.Current.CancellationToken; await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct);
        var source = Legacy(Snapshot(await Seed(fixture, ct)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RequestLocationRecovery.ImportAsync(fixture.Db,
            [source with { Payload = "corrupt"u8.ToArray() }], ct));
        Assert.Contains("IMPORT-HASH", error.Message);
        Assert.False(await fixture.Db.RequestLocations.AnyAsync(ct)); Assert.False(await fixture.Db.RequestLocationImportStates.AnyAsync(ct));
    }
    [Fact]
    public async Task ImportRefusesOrphanedHistoryWithoutPartiallySavingValidRecords()
    {
        var ct = TestContext.Current.CancellationToken; await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct);
        var known = Legacy(Snapshot(await Seed(fixture, ct))); var unknown = Legacy(Snapshot(Guid.NewGuid()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RequestLocationRecovery.ImportAsync(fixture.Db, [known, unknown], ct));
        Assert.Contains("IMPORT-ORPHAN", error.Message);
        Assert.False(await fixture.Db.RequestLocations.AnyAsync(ct)); Assert.False(await fixture.Db.RequestLocationImportStates.AnyAsync(ct));
    }
    [Fact]
    public async Task NewerPostgresGeographySurvivesImportAndLocationChangesEnqueueAtomicRefreshEvents()
    {
        var ct = TestContext.Current.CancellationToken; await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct); var id = await Seed(fixture, ct);
        await new RequestLocationService(fixture.Db).ResolveAndSaveAsync(id, "Kingston", null, null, ct);
        await RequestLocationRecovery.ImportAsync(fixture.Db, [Legacy(Snapshot(id))], ct);
        Assert.Equal("Kingston", (await fixture.Db.RequestLocations.SingleAsync(ct)).Parish);
        await fixture.Db.OutboxMessages.ExecuteDeleteAsync(ct);
        await using (var transaction = await fixture.Db.Database.BeginTransactionAsync(ct))
        {
            await fixture.Db.RequestLocations.Where(x => x.RequestId == id).ExecuteUpdateAsync(set => set.SetProperty(x => x.Community, "Changed"), ct);
            Assert.True(await fixture.Db.OutboxMessages.AnyAsync(x => x.MessageType == ManagementDashboardAggregateRefresher.MessageType, ct));
            await transaction.RollbackAsync(ct);
        }
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.OutboxMessages.AnyAsync(ct));
        Assert.Null((await fixture.Db.RequestLocations.SingleAsync(ct)).Community);
    }
    private static WebApplication Production(PostgreSqlTestDatabase fixture)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Services.AddScoped(_ => fixture.CreateSiblingContext()); return builder.Build();
    }
    [Fact]
    public async Task ProductionRefusesUnpreparedSchemaAndRequiresVerifiedGeographyRecovery()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var baseline = await PostgreSqlTestDatabase.CreateAsync(ct, "20261106170310_ManagementDashboardAggregate"))
        {
            await using var app = Production(baseline);
            Assert.Contains("RP-LOCATION-RECOVERY-001", (await Assert.ThrowsAsync<InvalidOperationException>(() => StartupTasks.ApplyDatabaseMigrationsAsync(app))).Message);
        }
        await using var current = await PostgreSqlTestDatabase.CreateAsync(ct);
        await using var prepared = Production(current);
        Assert.Contains("RP-LOCATION-RECOVERY-002", (await Assert.ThrowsAsync<InvalidOperationException>(() => StartupTasks.ApplyDatabaseMigrationsAsync(prepared))).Message);
        await RequestLocationRecovery.ImportAsync(current.Db, [], ct);
        await StartupTasks.ApplyDatabaseMigrationsAsync(prepared);
    }
    [Fact]
    public async Task VerifiedImportReleasesOldRetryDelayWithoutFalselyReportingDashboardReadiness()
    {
        var ct = TestContext.Current.CancellationToken; await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct);
        var id = await Seed(fixture, ct);
        await using var workerDb = fixture.CreateSiblingContext();
        var refresher = new ManagementDashboardAggregateRefresher(workerDb, new ManagementDashboardService(workerDb, Options.Create(new ManagementDashboardOptions())));
        Assert.True(await refresher.RefreshAsync(ct));
        var row = await fixture.Db.ManagementDashboardAggregates.SingleAsync(ct);
        string? previous = row.AggregateData; long version = row.SnapshotVersion;
        row.RefreshStatus = "Failed"; row.LastError = "Previous provider failure"; row.AttemptCount = 10; row.NextRefreshUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        await fixture.Db.SaveChangesAsync(ct);
        await RequestLocationRecovery.ImportAsync(fixture.Db, [Legacy(Snapshot(id))], ct);
        Assert.Equal(0, row.AttemptCount); Assert.True(row.NextRefreshUtc <= DateTimeOffset.UtcNow);
        Assert.Equal(previous, row.AggregateData); Assert.Equal(version, row.SnapshotVersion); Assert.Equal("Failed", row.RefreshStatus);
        var state = new RequestPortal.Web.Services.ManagementDashboardWorkerState(); state.Set("Waiting", DateTimeOffset.UtcNow);
        Assert.False((await RequestPortal.Web.Services.ManagementDashboardReadiness.ReadAsync(fixture.Db, state, ct)).Ready);
        workerDb.ChangeTracker.Clear(); Assert.True(await refresher.RefreshAsync(ct));
        var ready = await RequestPortal.Web.Services.ManagementDashboardReadiness.ReadAsync(fixture.Db, state, ct);
        Assert.True(ready.Ready); Assert.Equal(version + 1, ready.SnapshotVersion);
    }
    [Fact]
    public async Task ImportCannotRaceAnActiveDashboardCalculationAndRetriesAfterItsLockIsReleased()
    {
        var ct = TestContext.Current.CancellationToken; await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct);
        var source = Legacy(Snapshot(await Seed(fixture, ct)));
        await using (var worker = fixture.CreateSiblingContext())
        await using (var transaction = await worker.Database.BeginTransactionAsync(ct))
        {
            await worker.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7294321651::bigint)", ct);
            Assert.Contains("IMPORT-BUSY", (await Assert.ThrowsAsync<InvalidOperationException>(() => RequestLocationRecovery.ImportAsync(fixture.Db, [source], ct))).Message);
            Assert.False(await fixture.Db.RequestLocations.AnyAsync(ct)); Assert.False(await fixture.Db.RequestLocationImportStates.AnyAsync(ct));
            await transaction.RollbackAsync(ct);
        }
        Assert.Equal(1, await RequestLocationRecovery.ImportAsync(fixture.Db, [source], ct));
    }
    [Fact]
    public async Task ThrottledLoopPauseKeepsTheFailedSnapshotAndRestoresOnlyItsOwnSchedule()
    {
        var ct = TestContext.Current.CancellationToken; await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct);
        await fixture.Db.ManagementDashboardAggregates.ExecuteUpdateAsync(set => set.SetProperty(x => x.RefreshStatus, "Failed")
            .SetProperty(x => x.LastError, "Original provider failure").SetProperty(x => x.AttemptCount, 10), ct);
        var before = await fixture.Db.ManagementDashboardAggregates.AsNoTracking().SingleAsync(ct);
        var pause = await DashboardRecoveryPause.PauseAsync(fixture.Db, ct);
        var held = await fixture.Db.ManagementDashboardAggregates.AsNoTracking().SingleAsync(ct);
        Assert.Equal(before.AggregateData, held.AggregateData); Assert.Equal(before.SnapshotVersion, held.SnapshotVersion);
        Assert.Equal("Failed", held.RefreshStatus); Assert.Equal(before.LastError, held.LastError);
        Assert.True(held.NextRefreshUtc > DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(await DashboardRecoveryPause.ResumeAsync(fixture.Db, pause, ct));
        Assert.Equal(before.NextRefreshUtc, (await fixture.Db.ManagementDashboardAggregates.AsNoTracking().SingleAsync(ct)).NextRefreshUtc);
        var again = await DashboardRecoveryPause.PauseAsync(fixture.Db, ct);
        await RequestLocationRecovery.ImportAsync(fixture.Db, [], ct);
        Assert.False(await DashboardRecoveryPause.ResumeAsync(fixture.Db, again, ct));
        Assert.True((await fixture.Db.ManagementDashboardAggregates.AsNoTracking().SingleAsync(ct)).NextRefreshUtc <= DateTimeOffset.UtcNow);
    }
}
