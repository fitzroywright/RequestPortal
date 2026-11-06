using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Common.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Management;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Management;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class ManagementDashboardCacheTests(ITestOutputHelper output)
{
#pragma warning disable CS0618
    private sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }
#pragma warning restore CS0618
    public class Storage : DispatchProxy
    {
        public int Reads, Active, Maximum;
        public int Delay = 2;
        public bool Fail;
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
            try { await Task.Delay(Delay, ct); if (Fail) throw new InvalidOperationException("Fixture failure"); }
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
    private static ManagementDashboardService Service(PostgreSqlTestDatabase fixture, Storage storage, IMemoryCache? cache, int concurrency=16) =>
        new(fixture.CreateSiblingContext(), (IFileStorage)storage, Options.Create(new ManagementDashboardOptions {LocationReadConcurrency=concurrency}),cache);

    [Fact]
    public async Task ConcurrentScopesShareSnapshotExpireAndRefreshAllMetrics()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,32);
        var clock=new Clock();using var cache=new MemoryCache(new MemoryCacheOptions {Clock=clock});
        var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();
        var results=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>Service(fixture,storage,cache).GetDashboardAsync(ct)));
        Assert.All(results,r=>Assert.Same(results[0],r));Assert.Equal(32,storage.Reads);
        Assert.Equal(32,results[0].Parishes.Single(x=>x.Parish=="St. Catherine").ExactLocations);
        var changedId=fixture.Db.AssistanceRequests.First().Id;
        await fixture.Db.AssistanceRequests.Where(r=>r.Id==changedId).ExecuteUpdateAsync(set=>set.SetProperty(r=>r.Status,RequestStatus.Rejected),ct);
        clock.UtcNow=clock.UtcNow.AddSeconds(44);
        Assert.Same(results[0],await Service(fixture,storage,cache).GetDashboardAsync(ct));
        clock.UtcNow=clock.UtcNow.AddSeconds(2);
        var refreshed=await Service(fixture,storage,cache).GetDashboardAsync(ct);
        Assert.NotSame(results[0],refreshed);Assert.Equal(64,storage.Reads);
        Assert.Equal(results[0].Executive.Outstanding-1,refreshed.Executive.Outstanding);
        // A filtered scope must not receive the shared unfiltered aggregate.
        var filtered=await Service(fixture,storage,cache).GetDashboardAsync(new ManagementDashboardFilter(RequestId:Guid.NewGuid()),ct);
        Assert.Equal(0,filtered.Statuses.Sum(x=>x.Count));
    }
    [Fact]
    public async Task FailureAndCancellationDoNotPoisonCacheOrRefreshLock()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,16);
        using var cache=new MemoryCache(new MemoryCacheOptions());var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();storage.Fail=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Service(fixture,storage,cache).GetDashboardAsync(ct));
        storage.Fail=false;storage.Delay=20;
        var leader=Service(fixture,storage,cache).GetDashboardAsync(ct);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Service(fixture,storage,cache).GetDashboardAsync(cancelled.Token));
        var result=await leader;Assert.Same(result,await Service(fixture,storage,cache).GetDashboardAsync(ct));
    }
    [Fact]
    public async Task ColdAndWarmBenchmarkPreservesEveryAggregateAndBoundsStorageReads()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);await Seed(fixture,1344);
        var samples=new List<ManagementDashboardSnapshot>();
        foreach(var sample in new[]{(Concurrency:8,Cached:false),(Concurrency:8,Cached:true),(Concurrency:16,Cached:true)})
        {
            int concurrency=sample.Concurrency;
            using var memory=new MemoryCache(new MemoryCacheOptions());IMemoryCache? cache=sample.Cached?memory:null;var storage=(Storage)DispatchProxy.Create<IFileStorage,Storage>();
            // Warm query compilation outside the measured sample, without storage reads.
            await Service(fixture,storage,cache,concurrency).GetDashboardAsync(new ManagementDashboardFilter(RequestId:Guid.NewGuid()),ct);
            var watch=Stopwatch.StartNew();var service=Service(fixture,storage,cache,concurrency);
            var cold=await service.GetDashboardAsync(ct);double coldMs=watch.Elapsed.TotalMilliseconds;
            watch.Restart();var warm=await service.GetDashboardAsync(ct);double warmMs=watch.Elapsed.TotalMilliseconds;
            if(sample.Cached)Assert.Same(cold,warm);
            Assert.Equal(sample.Cached?1344:2688,storage.Reads);Assert.InRange(storage.Maximum,1,concurrency);
            samples.Add(cold);
            output.WriteLine($"1344 requests, simulated storage 2ms per operation, cached={sample.Cached} concurrency={concurrency}: cold={coldMs:F2}ms warm={warmMs:F3}ms metadataReads={storage.Reads}");
        }
        foreach(var sample in samples.Skip(1))
            Assert.Equal(JsonSerializer.Serialize(samples[0] with {GeneratedUtc=default}),JsonSerializer.Serialize(sample with {GeneratedUtc=default}));
    }
}
