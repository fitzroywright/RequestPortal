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

[Collection("Dashboard advisory lock")]
public sealed class ManagementDashboardAggregateTests(ITestOutputHelper output)
{
    public sealed class Control
    {
        public int Builds;
        public int Delay = 2;
        public bool Fail;
        public TaskCompletionSource? Entered, Release;
    }
    private sealed class ControlledBuilder(IManagementDashboardSnapshotBuilder inner, Control control) : IManagementDashboardSnapshotBuilder
    {
        public async Task<ManagementDashboardSnapshot> BuildAggregateAsync(CancellationToken ct=default)
        {
            Interlocked.Increment(ref control.Builds);
            if(control.Entered is not null) {control.Entered.TrySetResult();await control.Release!.Task.WaitAsync(ct);}
            await Task.Delay(control.Delay,ct);
            if(control.Fail)throw new InvalidOperationException("Fixture database calculation failed.");
            return await inner.BuildAggregateAsync(ct);
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
            fixture.Db.RequestLocations.Add(new PersistedRequestLocation {RequestId=r.Id,Parish="St. Catherine",Community="Fixture",
                Latitude=18m,Longitude=-77m,Precision=LocationPrecision.Exact,ResolvedUtc=now});
        }
        await fixture.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
    private static ManagementDashboardService Calculator(PostgreSqlTestDatabase fixture,Control control,int concurrency=16) =>
        new(fixture.CreateSiblingContext(),Options.Create(new ManagementDashboardOptions {LocationReadConcurrency=concurrency}));
    private static ManagementDashboardAggregateRefresher Refresher(PostgreSqlTestDatabase fixture,Control control,TimeProvider? clock=null)
    {
        var db=fixture.CreateSiblingContext();
        return new(db,new ControlledBuilder(new ManagementDashboardService(db,Options.Create(new ManagementDashboardOptions()),clock),control),clock);
    }
    private static PersistentManagementDashboardService Reader(PostgreSqlTestDatabase fixture,Control control) =>
        new(fixture.CreateSiblingContext(),Calculator(fixture,control));
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
    [Fact]
    public async Task OperatorSourceCountsCatalogChecksAndReadOnlyTransactionReconcile()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,2);
        var control=new Control();
        await Refresher(fixture,control).RefreshAsync(ct);
        var snapshot=await Reader(fixture,control).GetDashboardAsync(ct);
        await using var tx=await fixture.Db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,ct);
        await fixture.Db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY",ct);
        var evidence=await ManagementDashboardSourceVerification.ReadAsync(fixture.Db,snapshot,ct);
        Assert.True(evidence.Reconciled);Assert.Equal(2,evidence.Requests);Assert.Equal(2,evidence.Outstanding);
        Assert.True(evidence.MigrationPrivileges);Assert.Equal(11,evidence.DashboardTriggers);Assert.True(evidence.TriggerFunctionPresent);
        Assert.False(string.IsNullOrWhiteSpace(evidence.SchemaOwner));
        var invalid=snapshot with {Executive=snapshot.Executive with {Outstanding=99}};
        Assert.False((await ManagementDashboardSourceVerification.ReadAsync(fixture.Db,invalid,ct)).Reconciled);
        await tx.RollbackAsync(ct);
    }

    [Fact]
    public async Task ReadinessReportsSnapshotAgeFailureAndWorkerWithoutRebuilding()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);
        var control=new Control();
        var state=new RequestPortal.Web.Services.ManagementDashboardWorkerState();
        state.Set("Waiting",DateTimeOffset.UtcNow);
        var empty=await RequestPortal.Web.Services.ManagementDashboardReadiness.ReadAsync(fixture.Db,state,ct);
        Assert.False(empty.Ready);Assert.False(empty.AggregateInitialized);Assert.Equal(0,empty.SnapshotVersion);
        await Refresher(fixture,control).RefreshAsync(ct);
        var ready=await RequestPortal.Web.Services.ManagementDashboardReadiness.ReadAsync(fixture.Db,state,ct);
        Assert.True(ready.Ready);Assert.True(ready.AggregateInitialized);Assert.Equal(1,ready.SnapshotVersion);
        Assert.NotNull(ready.LastSuccessfulRefreshUtc);Assert.InRange(ready.SnapshotAgeSeconds!.Value,0,120);
        await fixture.Db.ManagementDashboardAggregates.ExecuteUpdateAsync(set=>set.SetProperty(x=>x.LastUpdatedUtc,DateTimeOffset.UtcNow.AddMinutes(-3)),ct);
        Assert.False((await RequestPortal.Web.Services.ManagementDashboardReadiness.ReadAsync(fixture.Db,state,ct)).Ready);
        await fixture.Db.ManagementDashboardAggregates.ExecuteUpdateAsync(set=>set.SetProperty(x=>x.RefreshStatus,"Failed"),ct);
        Assert.True((await RequestPortal.Web.Services.ManagementDashboardReadiness.ReadAsync(fixture.Db,state,ct)).RefreshFailed);
        Assert.Equal(1,control.Builds); // Readiness probes never invoke another build.
    }

    [Fact]
    public async Task MissingSingletonIsInitializedByWorkerRefreshOnly()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);
        await fixture.Db.ManagementDashboardAggregates.ExecuteDeleteAsync(ct);
        var control=new Control();
        await Assert.ThrowsAsync<ManagementDashboardInitializingException>(()=>Reader(fixture,control).GetDashboardAsync(ct));
        Assert.True(await Refresher(fixture,control).RefreshAsync(ct));
        Assert.Equal(1,(await Reader(fixture,control).GetDashboardAsync(ct)).SnapshotVersion);
    }

    [Fact]
    public async Task NoSnapshotAndInvalidJsonNeverTriggerHistoricalReadsOrHideDatabaseFailures()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);
        var control=new Control();
        await Assert.ThrowsAsync<ManagementDashboardInitializingException>(()=>Reader(fixture,control).GetDashboardAsync(ct));
        Assert.Equal(0,control.Builds);
        foreach (string json in new[] { "{}", "null", "[]" })
        {
            await fixture.Db.ManagementDashboardAggregates.ExecuteUpdateAsync(set=>set.SetProperty(x=>x.SnapshotVersion,1).SetProperty(x=>x.AggregateData,json),ct);
            await Assert.ThrowsAsync<ManagementDashboardInitializingException>(()=>Reader(fixture,control).GetDashboardAsync(ct));
        }
        Assert.Equal(0,control.Builds);
        await fixture.Db.Database.ExecuteSqlRawAsync("DROP TABLE management_dashboard_aggregate",ct);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>Reader(fixture,control).GetDashboardAsync(ct));
    }

    [Fact]
    public async Task PublishedSnapshotRemainsReadableWhileRebuildIsBlocked()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1);
        var control=new Control();var clock=new Clock();
        await Refresher(fixture,control,clock).RefreshAsync(ct);
        var before=await Reader(fixture,control).GetDashboardAsync(ct);
        clock.Now=clock.Now.AddMinutes(2);
        control.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);control.Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var rebuild=Refresher(fixture,control,clock).RefreshAsync(ct);
        await control.Entered.Task.WaitAsync(ct);
        try
        {
            var read=await Reader(fixture,control).GetDashboardAsync(ct).WaitAsync(TimeSpan.FromSeconds(2),ct);
            Assert.Equal(before.SnapshotVersion,read.SnapshotVersion);
            Assert.Equal(before.LastUpdatedUtc,read.LastUpdatedUtc);
        }
        finally {control.Release.TrySetResult();}
        Assert.True(await rebuild);
    }

    [Fact]
    public async Task InitialBuildCoalescesEventsAndHttpReadsOnlyPublishedJson()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,32);
        var control=new Control();
        Assert.True(await Refresher(fixture,control).RefreshAsync(ct));
        var first=await Reader(fixture,control).GetDashboardAsync(ct);Assert.Equal(32,first.Statuses.Sum(x=>x.Count));
        Assert.Equal(32,first.Parishes.Single(x=>x.Parish=="St. Catherine").ExactLocations);
        Assert.Equal(1,first.SnapshotVersion);Assert.Equal(1,control.Builds);
        Assert.False(await Refresher(fixture,control).RefreshAsync(ct));
        control.Fail=true;
        var results=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Reader(fixture,control).GetDashboardAsync(ct)));
        Assert.All(results,r=>Assert.Equal(first.SnapshotVersion,r.SnapshotVersion));Assert.Equal(1,control.Builds);
        Assert.False(await fixture.Db.OutboxMessages.AnyAsync(m=>m.MessageType==ManagementDashboardAggregateRefresher.MessageType && m.ProcessedUtc==null,ct));
    }
    [Fact]
    public async Task TransactionalEventsConcurrentWorkersScheduledReconciliationAndFailureRecovery()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,32);
        var control=new Control();var clock=new Clock();
        Assert.True(await Refresher(fixture,control,clock).RefreshAsync(ct));
        var id=fixture.Db.AssistanceRequests.First().Id;
        await fixture.Db.AssistanceRequests.Where(r=>r.Id==id).ExecuteUpdateAsync(set=>set.SetProperty(r=>r.Status,RequestStatus.Rejected),ct);
        Assert.True(await fixture.Db.OutboxMessages.AnyAsync(m=>m.MessageType==ManagementDashboardAggregateRefresher.MessageType && m.ProcessedUtc==null,ct));
        var workers=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Refresher(fixture,control,clock).RefreshAsync(ct)));
        Assert.Equal(1,workers.Count(x=>x));Assert.Equal(2,control.Builds);
        var current=await Reader(fixture,control).GetDashboardAsync(ct);Assert.Equal(31,current.Executive.Outstanding);
        // Time-dependent statistics are refreshed even with no new outbox events.
        clock.Now=clock.Now.AddMinutes(16);control.Fail=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Refresher(fixture,control,clock).RefreshAsync(ct));
        var stale=await Reader(fixture,control).GetDashboardAsync(ct);Assert.Equal(current.SnapshotVersion,stale.SnapshotVersion);
        Assert.Equal("Failed",stale.RefreshStatus);Assert.Equal(current.LastUpdatedUtc,stale.LastUpdatedUtc);
        control.Fail=false;clock.Now=clock.Now.AddMinutes(1);
        Assert.True(await Refresher(fixture,control,clock).RefreshAsync(ct));
        var recovered=await Reader(fixture,control).GetDashboardAsync(ct);Assert.Equal("Current",recovered.RefreshStatus);
        Assert.True(recovered.LastReconciledUtc>current.LastReconciledUtc);
    }
    [Fact]
    public async Task RolledBackBusinessChangeDoesNotLeaveRefreshEvents()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1);
        var control=new Control();await Refresher(fixture,control).RefreshAsync(ct);
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
        var control=new Control();
        var timer=Stopwatch.StartNew();var baseline=await Calculator(fixture,control).GetDashboardAsync(ct);double baselineMs=timer.Elapsed.TotalMilliseconds;
        await Refresher(fixture,control).RefreshAsync(ct);int reads=control.Builds;
        timer.Restart();var cold=await Reader(fixture,control).GetDashboardAsync(ct);double coldMs=timer.Elapsed.TotalMilliseconds;
        timer.Restart();var warm=await Reader(fixture,control).GetDashboardAsync(ct);double warmMs=timer.Elapsed.TotalMilliseconds;
        Assert.Equal(reads,control.Builds);
        Assert.Equal(JsonSerializer.Serialize(baseline with {GeneratedUtc=default}),JsonSerializer.Serialize(cold with {GeneratedUtc=default,SnapshotVersion=0,LastUpdatedUtc=null,LastReconciledUtc=null}));
        Assert.Equal(cold.SnapshotVersion,warm.SnapshotVersion);
        output.WriteLine($"1344 synthetic requests: calculator={baselineMs:F2}ms PostgreSQL snapshot cold={coldMs:F2}ms warm={warmMs:F2}ms; HTTP control reads=0");
    }
    [Fact]
    public async Task EventsCommittedDuringBuildSurvivePublicationAndCancelledBuildReleasesLock()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,16);
        var control=new Control();var clock=new Clock();await Refresher(fixture,control,clock).RefreshAsync(ct);
        clock.Now=clock.Now.AddMinutes(2);
        control.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);control.Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var build=Refresher(fixture,control,clock).RefreshAsync(ct);await control.Entered.Task.WaitAsync(ct);
        await using(var change=fixture.CreateSiblingContext())
            await change.AssistanceRequests.Where(r=>r.Id==fixture.Db.AssistanceRequests.First().Id)
                .ExecuteUpdateAsync(s=>s.SetProperty(r=>r.Status,RequestStatus.Rejected),ct);
        control.Release.TrySetResult();Assert.True(await build);
        Assert.True(await fixture.Db.OutboxMessages.AnyAsync(m=>m.MessageType==ManagementDashboardAggregateRefresher.MessageType && m.ProcessedUtc==null,ct));
        control.Entered=null;control.Release=null;
        Assert.True(await Refresher(fixture,control,clock).RefreshAsync(ct));
        var current=await Reader(fixture,control).GetDashboardAsync(ct);Assert.Equal(15,current.Executive.Outstanding);
        clock.Now=clock.Now.AddMinutes(2);control.Delay=100;
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct);cancel.CancelAfter(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Refresher(fixture,control,clock).RefreshAsync(cancel.Token));
        Assert.Equal(current.SnapshotVersion,(await Reader(fixture,control).GetDashboardAsync(ct)).SnapshotVersion);
        control.Delay=2;Assert.True(await Refresher(fixture,control,clock).RefreshAsync(ct));
    }
    [Fact]
    public async Task AuthoritativeLocationDataCannotBeDestroyedByApplicationRollback()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1);
        Assert.False(fixture.Db.Database.HasPendingModelChanges());
        var migrator=Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(fixture.Db);
        await Assert.ThrowsAsync<NotSupportedException>(()=>migrator.MigrateAsync("20261106170310_ManagementDashboardAggregate",ct));
        Assert.Equal(1,await fixture.Db.AssistanceRequests.CountAsync(ct));
        Assert.Equal(1,await fixture.Db.RequestLocations.CountAsync(ct));
    }

    [Fact]
    public async Task ScheduledRefreshAdvancesCalendarAndOverdueStatisticsWithoutNewActivity()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1);
        var control=new Control();var clock=new Clock();
        await fixture.Db.AssistanceRequests.ExecuteUpdateAsync(s=>s.SetProperty(r=>r.SubmittedUtc,(DateTimeOffset?)clock.Now),ct);
        await Refresher(fixture,control,clock).RefreshAsync(ct);
        Assert.Equal(1,(await Reader(fixture,control).GetDashboardAsync(ct)).Executive.Today);
        clock.Now=clock.Now.AddDays(16);await Refresher(fixture,control,clock).RefreshAsync(ct);
        var next=await Reader(fixture,control).GetDashboardAsync(ct);Assert.Equal(0,next.Executive.Today);Assert.Equal(1,next.Attention.Overdue);
    }

    [Fact]
    public async Task DatabaseBuildFailureRetainsPreviousSnapshotAndGeography()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1);
        var control=new Control();var clock=new Clock();await Refresher(fixture,control,clock).RefreshAsync(ct);
        var valid=await Reader(fixture,control).GetDashboardAsync(ct);clock.Now=clock.Now.AddMinutes(2);control.Fail=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Refresher(fixture,control,clock).RefreshAsync(ct));
        var retained=await Reader(fixture,control).GetDashboardAsync(ct);Assert.Equal(valid.SnapshotVersion,retained.SnapshotVersion);
        Assert.Equal(1,retained.Parishes.Single(x=>x.Parish=="St. Catherine").ExactLocations);Assert.Equal("Failed",retained.RefreshStatus);
    }

}
