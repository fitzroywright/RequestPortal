using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Common.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Management;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Management;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class ManagementDashboardAggregateTests(ITestOutputHelper output)
{
    public class Storage : DispatchProxy
    {
        public int Reads, Active, Maximum;
        public int Delay = 2;
        public bool Fail;
        public bool StorageUnavailable;
        public TaskCompletionSource? Entered,Release;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "GetMetadataAsync" => Metadata((string)args![0]!, (CancellationToken)args[1]!),
            "OpenReadAsync" => Open((string)args![0]!, (CancellationToken)args[1]!),
            _ => throw new InvalidOperationException("Unexpected storage write/operation")
        };
        private async Task<StoredFile?> Metadata(string key, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            int active = Interlocked.Increment(ref Active);
            int observed;
            do { observed = Maximum; } while (active > observed && Interlocked.CompareExchange(ref Maximum, active, observed) != observed);
            try { if(Entered is not null) {Entered.TrySetResult();await Release!.Task.WaitAsync(ct);} await Task.Delay(Delay, ct); if (Fail) throw new InvalidOperationException("Fixture failure"); if(StorageUnavailable) throw new StorageException("STORAGE-UNAVAILABLE","Fixture unavailable"); }
            finally { Interlocked.Decrement(ref Active); }
            return new(key, 1, "application/json", "resolved-location.json", "", DateTimeOffset.UtcNow, "fixture", 1, StorageStatus.Stored, new Dictionary<string,string>());
        }
        private async Task<Stream> Open(string key, CancellationToken ct)
        {
            await Task.Delay(Delay, ct);
            return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new {
                requestId=Guid.ParseExact(key.Split('/')[2],"N"), parish="St. Catherine", community="Fixture", precision=3,
                resolvedUtc=DateTimeOffset.UtcNow }));
        }
    }
    private static async Task Seed(PostgreSqlTestDatabase fixture, int count)
    {
        var actor=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        var org=new Organization(Guid.NewGuid(),"Cache fixture","JM",actor,now);
        fixture.Db.Organizations.Add(org);
        for(int i=0;i<count;i++)
        {
            var r=new AssistanceRequest(Guid.NewGuid(),"CACHE-"+i,org.Id,actor,now.AddDays(-2));
            r.SetKind(RequestKind.Project);r.SetSummary("Fixture");r.Submit(actor,now.AddDays(-1));
            fixture.Db.AssistanceRequests.Add(r);
        }
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
    private static ManagementDashboardService Calculator(PostgreSqlTestDatabase fixture,Storage storage,int concurrency=16) =>
        new(fixture.CreateSiblingContext(),(IFileStorage)storage,Options.Create(new ManagementDashboardOptions {LocationReadConcurrency=concurrency}));
    private static ManagementDashboardAggregateRefresher Refresher(PostgreSqlTestDatabase fixture,Storage storage,TimeProvider? clock=null)
    {
        var db=fixture.CreateSiblingContext();
        return new(db,new(db,(IFileStorage)storage,Options.Create(new ManagementDashboardOptions()),clock),clock);
    }
    private static PersistentManagementDashboardService Reader(PostgreSqlTestDatabase fixture,Storage storage) =>
        new(fixture.CreateSiblingContext(),Calculator(fixture,storage));
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    [Fact]
    public async Task OperatorSourceCountsCatalogChecksAndReadOnlyTransactionReconcile()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,2);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();
        await Refresher(fixture,storage).RefreshAsync(ct);
        var snapshot=await Reader(fixture,storage).GetDashboardAsync(ct);
        await using var tx=await fixture.Db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        await fixture.Db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY",ct);
        var evidence=await ManagementDashboardSourceVerification.ReadAsync(fixture.Db,snapshot,ct);
        Assert.True(evidence.Reconciled);Assert.Equal(2,evidence.Requests);Assert.Equal(2,evidence.Outstanding);
        Assert.True(evidence.MigrationPrivileges);Assert.Equal(10,evidence.DashboardTriggers);Assert.True(evidence.TriggerFunctionPresent);
        Assert.False(string.IsNullOrWhiteSpace(evidence.SchemaOwner));
        var invalid=snapshot with {Executive=snapshot.Executive with {Outstanding=99}};
        Assert.False((await ManagementDashboardSourceVerification.ReadAsync(fixture.Db,invalid,ct)).Reconciled);
        await tx.RollbackAsync(ct);
    }

    [Fact]
    public async Task ReadinessReportsSnapshotAgeFailureAndWorkerWithoutRebuilding()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();
        var state=new RequestPortal.Web.Services.ManagementDashboardWorkerState();
        state.Set("Waiting",DateTimeOffset.UtcNow);
        var empty=await RequestPortal.Web.Services.ManagementDashboardReadiness.ReadAsync(fixture.Db,state,ct);
        Assert.False(empty.Ready);Assert.False(empty.AggregateInitialized);Assert.Equal(0,empty.SnapshotVersion);
        await Refresher(fixture,storage).RefreshAsync(ct);
        var ready=await RequestPortal.Web.Services.ManagementDashboardReadiness.ReadAsync(fixture.Db,state,ct);
        Assert.True(ready.Ready);Assert.True(ready.AggregateInitialized);Assert.Equal(1,ready.SnapshotVersion);
        Assert.NotNull(ready.LastSuccessfulRefreshUtc);Assert.InRange(ready.SnapshotAgeSeconds!.Value,0,120);
        await fixture.Db.ManagementDashboardAggregates.ExecuteUpdateAsync(set=>set.SetProperty(x=>x.LastUpdatedUtc,DateTimeOffset.UtcNow.AddMinutes(-3)),ct);
        Assert.False((await RequestPortal.Web.Services.ManagementDashboardReadiness.ReadAsync(fixture.Db,state,ct)).Ready);
        await fixture.Db.ManagementDashboardAggregates.ExecuteUpdateAsync(set=>set.SetProperty(x=>x.RefreshStatus,"Failed"),ct);
        Assert.True((await RequestPortal.Web.Services.ManagementDashboardReadiness.ReadAsync(fixture.Db,state,ct)).RefreshFailed);
        Assert.Equal(0,storage.Reads); // Empty source has no location work; probes never invoke it.
    }

    [Fact]
    public async Task MissingSingletonIsInitializedByWorkerRefreshOnly()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);
        await fixture.Db.ManagementDashboardAggregates.ExecuteDeleteAsync(ct);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();
        await Assert.ThrowsAsync<ManagementDashboardInitializingException>(()=>Reader(fixture,storage).GetDashboardAsync(ct));
        Assert.True(await Refresher(fixture,storage).RefreshAsync(ct));
        Assert.Equal(1,(await Reader(fixture,storage).GetDashboardAsync(ct)).SnapshotVersion);
    }

    [Fact]
    public async Task NoSnapshotAndInvalidJsonNeverTriggerHistoricalReadsOrHideDatabaseFailures()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();
        await Assert.ThrowsAsync<ManagementDashboardInitializingException>(()=>Reader(fixture,storage).GetDashboardAsync(ct));
        Assert.Equal(0,storage.Reads);
        foreach (string json in new[] { "{}", "null", "[]" })
        {
            await fixture.Db.ManagementDashboardAggregates.ExecuteUpdateAsync(set=>set.SetProperty(x=>x.SnapshotVersion,1).SetProperty(x=>x.AggregateData,json),ct);
            await Assert.ThrowsAsync<ManagementDashboardInitializingException>(()=>Reader(fixture,storage).GetDashboardAsync(ct));
        }
        Assert.Equal(0,storage.Reads);
        await fixture.Db.Database.ExecuteSqlRawAsync("DROP TABLE management_dashboard_aggregate",ct);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>Reader(fixture,storage).GetDashboardAsync(ct));
    }

    [Fact]
    public async Task PublishedSnapshotRemainsReadableWhileRebuildIsBlocked()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();var clock=new Clock();
        await Refresher(fixture,storage,clock).RefreshAsync(ct);
        var before=await Reader(fixture,storage).GetDashboardAsync(ct);
        clock.Now=clock.Now.AddMinutes(2);
        storage.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);storage.Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebuild=Refresher(fixture,storage,clock).RefreshAsync(ct);
        await storage.Entered.Task.WaitAsync(ct);
        try
        {
            var read=await Reader(fixture,storage).GetDashboardAsync(ct).WaitAsync(TimeSpan.FromSeconds(2),ct);
            Assert.Equal(before.SnapshotVersion,read.SnapshotVersion);
            Assert.Equal(before.LastUpdatedUtc,read.LastUpdatedUtc);
        }
        finally {storage.Release.TrySetResult();}
        Assert.True(await rebuild);
    }

    [Fact]
    public async Task InitialBuildCoalescesEventsAndHttpReadsOnlyPublishedJson()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,32);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();
        Assert.True(await Refresher(fixture,storage).RefreshAsync(ct));
        var first=await Reader(fixture,storage).GetDashboardAsync(ct);Assert.Equal(32,first.Statuses.Sum(x=>x.Count));
        Assert.Equal(32,first.Parishes.Single(x=>x.Parish=="St. Catherine").ExactLocations);
        Assert.Equal(1,first.SnapshotVersion);Assert.Equal(32,storage.Reads);
        Assert.False(await Refresher(fixture,storage).RefreshAsync(ct));
        storage.Fail=true;
        var results=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Reader(fixture,storage).GetDashboardAsync(ct)));
        Assert.All(results,r=>Assert.Equal(first.SnapshotVersion,r.SnapshotVersion));Assert.Equal(32,storage.Reads);
        Assert.False(await fixture.Db.OutboxMessages.AnyAsync(m=>m.MessageType==ManagementDashboardAggregateRefresher.MessageType && m.ProcessedUtc==null,ct));
    }
    [Fact]
    public async Task TransactionalEventsConcurrentWorkersScheduledReconciliationAndFailureRecovery()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,32);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();var clock=new Clock();
        Assert.True(await Refresher(fixture,storage,clock).RefreshAsync(ct));
        var id=fixture.Db.AssistanceRequests.First().Id;
        await fixture.Db.AssistanceRequests.Where(r=>r.Id==id).ExecuteUpdateAsync(set=>set.SetProperty(r=>r.Status,RequestStatus.Rejected),ct);
        Assert.True(await fixture.Db.OutboxMessages.AnyAsync(m=>m.MessageType==ManagementDashboardAggregateRefresher.MessageType && m.ProcessedUtc==null,ct));
        var workers=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Refresher(fixture,storage,clock).RefreshAsync(ct)));
        Assert.Equal(1,workers.Count(x=>x));Assert.Equal(64,storage.Reads);
        var current=await Reader(fixture,storage).GetDashboardAsync(ct);Assert.Equal(31,current.Executive.Outstanding);
        // Time-dependent statistics are refreshed even with no new outbox events.
        clock.Now=clock.Now.AddMinutes(16);storage.Fail=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Refresher(fixture,storage,clock).RefreshAsync(ct));
        var stale=await Reader(fixture,storage).GetDashboardAsync(ct);Assert.Equal(current.SnapshotVersion,stale.SnapshotVersion);
        Assert.Equal("Failed",stale.RefreshStatus);Assert.Equal(current.LastUpdatedUtc,stale.LastUpdatedUtc);
        storage.Fail=false;clock.Now=clock.Now.AddMinutes(1);
        Assert.True(await Refresher(fixture,storage,clock).RefreshAsync(ct));
        var recovered=await Reader(fixture,storage).GetDashboardAsync(ct);Assert.Equal("Current",recovered.RefreshStatus);
        Assert.True(recovered.LastReconciledUtc>current.LastReconciledUtc);
    }
    [Fact]
    public async Task RolledBackBusinessChangeDoesNotLeaveRefreshEvents()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();await Refresher(fixture,storage).RefreshAsync(ct);
        await using(var tx=await fixture.Db.Database.BeginTransactionAsync(ct))
        {
            await fixture.Db.AssistanceRequests.ExecuteUpdateAsync(s=>s.SetProperty(r=>r.Status,RequestStatus.Rejected),ct);
            Assert.True(await fixture.Db.OutboxMessages.AnyAsync(m=>m.MessageType==ManagementDashboardAggregateRefresher.MessageType && m.ProcessedUtc==null,ct));
            await tx.RollbackAsync(ct);
        }
        Assert.False(await fixture.Db.OutboxMessages.AnyAsync(m=>m.MessageType==ManagementDashboardAggregateRefresher.MessageType && m.ProcessedUtc==null,ct));
    }
    [Fact]
    public async Task BenchmarkPersistentReadHasNoHistoricalOrStorageScan()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1344);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();
        var timer=Stopwatch.StartNew();var baseline=await Calculator(fixture,storage).GetDashboardAsync(ct);double baselineMs=timer.Elapsed.TotalMilliseconds;
        await Refresher(fixture,storage).RefreshAsync(ct);int reads=storage.Reads;
        timer.Restart();var cold=await Reader(fixture,storage).GetDashboardAsync(ct);double coldMs=timer.Elapsed.TotalMilliseconds;
        timer.Restart();var warm=await Reader(fixture,storage).GetDashboardAsync(ct);double warmMs=timer.Elapsed.TotalMilliseconds;
        Assert.Equal(reads,storage.Reads);
        Assert.Equal(JsonSerializer.Serialize(baseline with {GeneratedUtc=default}),JsonSerializer.Serialize(cold with {GeneratedUtc=default,SnapshotVersion=0,LastUpdatedUtc=null,LastReconciledUtc=null}));
        Assert.Equal(cold.SnapshotVersion,warm.SnapshotVersion);
        output.WriteLine($"1344 synthetic requests: calculator={baselineMs:F2}ms PostgreSQL snapshot cold={coldMs:F2}ms warm={warmMs:F2}ms; HTTP storage reads=0");
    }
    [Fact]
    public async Task EventsCommittedDuringBuildSurvivePublicationAndCancelledBuildReleasesLock()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,16);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();var clock=new Clock();await Refresher(fixture,storage,clock).RefreshAsync(ct);
        clock.Now=clock.Now.AddMinutes(2);
        storage.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);storage.Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var build=Refresher(fixture,storage,clock).RefreshAsync(ct);await storage.Entered.Task.WaitAsync(ct);
        await using(var change=fixture.CreateSiblingContext())
            await change.AssistanceRequests.Where(r=>r.Id==fixture.Db.AssistanceRequests.First().Id)
                .ExecuteUpdateAsync(s=>s.SetProperty(r=>r.Status,RequestStatus.Rejected),ct);
        storage.Release.TrySetResult();Assert.True(await build);
        Assert.True(await fixture.Db.OutboxMessages.AnyAsync(m=>m.MessageType==ManagementDashboardAggregateRefresher.MessageType && m.ProcessedUtc==null,ct));
        storage.Entered=null;storage.Release=null;
        Assert.True(await Refresher(fixture,storage,clock).RefreshAsync(ct));
        var current=await Reader(fixture,storage).GetDashboardAsync(ct);Assert.Equal(15,current.Executive.Outstanding);
        clock.Now=clock.Now.AddMinutes(2);storage.Delay=100;
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct);cancel.CancelAfter(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Refresher(fixture,storage,clock).RefreshAsync(cancel.Token));
        Assert.Equal(current.SnapshotVersion,(await Reader(fixture,storage).GetDashboardAsync(ct)).SnapshotVersion);
        storage.Delay=2;Assert.True(await Refresher(fixture,storage,clock).RefreshAsync(ct));
    }
    [Fact]
    public async Task AdditiveMigrationCanRollBackAndReapplyWithoutChangingBusinessRecords()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1);
        Assert.False(fixture.Db.Database.HasPendingModelChanges());
        var migrator=Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(fixture.Db);
        await migrator.MigrateAsync("20261105070000_IntegrationExceptionDetails",ct);
        Assert.Equal(1,await fixture.Db.AssistanceRequests.CountAsync(ct));
        await migrator.MigrateAsync(cancellationToken:ct);
        Assert.Equal(1,await fixture.Db.AssistanceRequests.CountAsync(ct));
        Assert.Equal(0,(await fixture.Db.ManagementDashboardAggregates.AsNoTracking().SingleAsync(ct)).SnapshotVersion);
    }

    [Fact]
    public async Task ScheduledRefreshAdvancesCalendarAndOverdueStatisticsWithoutNewActivity()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();var clock=new Clock();
        await fixture.Db.AssistanceRequests.ExecuteUpdateAsync(s=>s.SetProperty(r=>r.SubmittedUtc,(DateTimeOffset?)clock.Now),ct);
        await Refresher(fixture,storage,clock).RefreshAsync(ct);
        Assert.Equal(1,(await Reader(fixture,storage).GetDashboardAsync(ct)).Executive.Today);
        clock.Now=clock.Now.AddDays(16);await Refresher(fixture,storage,clock).RefreshAsync(ct);
        var next=await Reader(fixture,storage).GetDashboardAsync(ct);Assert.Equal(0,next.Executive.Today);Assert.Equal(1,next.Attention.Overdue);
    }

    [Fact]
    public async Task StorageOutageRetainsPreviousSnapshotInsteadOfReplacingExactGeographyWithFallback()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1);
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();var clock=new Clock();await Refresher(fixture,storage,clock).RefreshAsync(ct);
        var valid=await Reader(fixture,storage).GetDashboardAsync(ct);clock.Now=clock.Now.AddMinutes(2);storage.StorageUnavailable=true;
        await Assert.ThrowsAsync<StorageException>(()=>Refresher(fixture,storage,clock).RefreshAsync(ct));
        var retained=await Reader(fixture,storage).GetDashboardAsync(ct);Assert.Equal(valid.SnapshotVersion,retained.SnapshotVersion);
        Assert.Equal(1,retained.Parishes.Single(x=>x.Parish=="St. Catherine").ExactLocations);Assert.Equal("Failed",retained.RefreshStatus);
    }

}
