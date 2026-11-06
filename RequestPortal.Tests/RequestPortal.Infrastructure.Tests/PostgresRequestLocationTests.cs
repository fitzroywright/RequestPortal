using Common.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Management;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Management;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Web.Startup;
using RequestPortal.StorageMigration;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class PostgresRequestLocationTests
{
    private static async Task<Guid> Seed(PostgreSqlTestDatabase fixture,CancellationToken ct)
    {
        var actor=Guid.NewGuid();var now=DateTimeOffset.UtcNow;var org=new Organization(Guid.NewGuid(),"Geo fixture","JM",actor,now);
        var request=new AssistanceRequest(Guid.NewGuid(),"GEO-1",org.Id,actor,now);request.SetKind(RequestKind.Project);request.SetSummary("Fixture");request.Submit(actor,now);
        fixture.Db.Organizations.Add(org);fixture.Db.AssistanceRequests.Add(request);await fixture.Db.SaveChangesAsync(ct);return request.Id;
    }
    [Fact]
    public async Task DashboardAndDrilldownsUsePostgresLocationsWithoutAnyFileProvider()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);var id=await Seed(fixture,ct);
        var service=new RequestLocationService(fixture.Db);
        await service.ResolveAndSaveAsync(id,"Half Way Tree, St. Andrew",18.0179,-76.7936,ct);
        var dashboard=new ManagementDashboardService(fixture.CreateSiblingContext(),Options.Create(new ManagementDashboardOptions()));
        var snapshot=await dashboard.BuildAggregateAsync(ct);
        var parish=Assert.Single(snapshot.Parishes,x=>x.Requests>0);Assert.Equal("St. Andrew",parish.Parish);Assert.Equal(1,parish.ExactLocations);
        var rows=await dashboard.GetRequestsAsync(new ManagementRequestFilter(),ct);
        Assert.Equal("Half Way Tree",Assert.Single(rows).Community);
        var changed=await fixture.Db.RequestLocations.SingleAsync(ct);changed.Parish="St. Catherine";await fixture.Db.SaveChangesAsync(ct);
        Assert.Equal(1,(await dashboard.BuildAggregateAsync(ct)).Parishes.Single(x=>x.Parish=="St. Catherine").Requests);
    }
    [Fact]
    public async Task LocationAndRefreshEventRollBackTogetherAndRawSqlChangesEnqueueUpdates()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);var id=await Seed(fixture,ct);
        await fixture.Db.OutboxMessages.ExecuteDeleteAsync(ct);
        await using(var tx=await fixture.Db.Database.BeginTransactionAsync(ct))
        {
            await new RequestLocationService(fixture.Db).ResolveAndSaveAsync(id,"Kingston",null,null,ct);
            Assert.True(await fixture.Db.OutboxMessages.AnyAsync(x=>x.MessageType==ManagementDashboardAggregateRefresher.MessageType,ct));
            await tx.RollbackAsync(ct);
        }
        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.RequestLocations.AnyAsync(ct));Assert.False(await fixture.Db.OutboxMessages.AnyAsync(ct));
        await new RequestLocationService(fixture.Db).ResolveAndSaveAsync(id,"Kingston",null,null,ct);
        await fixture.Db.OutboxMessages.ExecuteDeleteAsync(ct);
        await fixture.Db.RequestLocations.Where(x=>x.RequestId==id).ExecuteUpdateAsync(set=>set.SetProperty(x=>x.Community,"Changed"),ct);
        Assert.True(await fixture.Db.OutboxMessages.AnyAsync(x=>x.MessageType==ManagementDashboardAggregateRefresher.MessageType && x.ProcessedUtc==null,ct));
    }
    private static WebApplication Production(PostgreSqlTestDatabase fixture,string? authorization=null)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions {EnvironmentName="Production"});
        builder.Services.AddScoped(_=>fixture.CreateSiblingContext());
        if(authorization is not null)builder.Configuration["StorageCutover:AuthorizedPlanSha256"]=authorization;
        return builder.Build();
    }
    [Fact]
    public async Task ProductionCannotApplyTheNewMigrationWithoutAdministratorPreparation()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct,"20261106170310_ManagementDashboardAggregate");
        await using var app=Production(fixture);
        var error=await Assert.ThrowsAsync<InvalidOperationException>(()=>StartupTasks.ApplyDatabaseMigrationsAsync(app));
        Assert.Contains("RP-STORAGE-CUTOVER-001",error.Message);
        Assert.DoesNotContain((await fixture.Db.Database.GetAppliedMigrationsAsync(ct)),x=>x.EndsWith("_PersistRequestLocations",StringComparison.Ordinal));
    }
    [Fact]
    public async Task ProductionRequiresDurableImportEvidenceAndMatchingExplicitAuthorization()
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);
        await using(var missing=Production(fixture))
            Assert.Contains("RP-STORAGE-CUTOVER-002",(await Assert.ThrowsAsync<InvalidOperationException>(()=>StartupTasks.ApplyDatabaseMigrationsAsync(missing))).Message);
        string authorized=new('a',64);
        fixture.Db.RequestLocationImportStates.Add(new() {AuthorizedPlanSha256=authorized,SourceFingerprint=new('b',64),ReferenceFingerprint=new('c',64),VerifiedUtc=DateTimeOffset.UtcNow});
        await fixture.Db.SaveChangesAsync(ct);
        await using(var wrong=Production(fixture,new string('d',64)))
            Assert.Contains("RP-STORAGE-CUTOVER-002",(await Assert.ThrowsAsync<InvalidOperationException>(()=>StartupTasks.ApplyDatabaseMigrationsAsync(wrong))).Message);
        await using var valid=Production(fixture,authorized);await StartupTasks.ApplyDatabaseMigrationsAsync(valid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocationImportPreservesLegacyCoordinatesAndCommitsItsEvidenceAtomically(bool invalid)
    {
        var ct=TestContext.Current.CancellationToken;await using var fixture=await PostgreSqlTestDatabase.CreateAsync(ct);
        Guid id=await Seed(fixture,ct);
        string root=Path.Combine(Path.GetTempPath(),"rp-location-import-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string key=$"requestportal/locations/{id:N}/resolved-location.json";
            var original=new RequestLocationSnapshot(id,"Half Way Tree, St. Andrew","St. Andrew","Half Way Tree",18.0179123456789m,-76.7936123456789m,
                LocationPrecision.Exact,DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
            byte[] bytes=invalid?"{invalid-json"u8.ToArray():JsonSerializer.SerializeToUtf8Bytes(original,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            string hash=Convert.ToHexStringLower(SHA256.HashData(bytes));
            var metadata=new StoredFile(key,bytes.Length,"application/json","resolved-location.json",hash,DateTimeOffset.UtcNow,"legacy",1,
                StorageStatus.Stored,new Dictionary<string,string>{{"RequestId",id.ToString("D")}});
            string payload=Path.Combine(root,"payload");await File.WriteAllBytesAsync(payload,bytes,ct);
            string destination=Path.Combine(root,"files");Directory.CreateDirectory(destination);
            await new NetworkFolderImporter(destination).PublishCurrentAsync(metadata,payload,ct);
            string sourceHash=new('a',64),referenceHash=new('b',64),authorization=new('c',64);
            var inventory=new SourceInventory(sourceHash,DateTimeOffset.UtcNow,"drive","Portal",[],[new(metadata,"m","etag",null,[])],[],false);
            if(invalid)
            {
                var error=await Assert.ThrowsAsync<MigrationSourceException>(()=>DatabaseReconciliation.ImportLocationsAsync(fixture.Db,inventory,destination,authorization,referenceHash,ct));
                Assert.Equal("LEGACY_LOCATION_JSON_INVALID",error.Code);
                fixture.Db.ChangeTracker.Clear();
                Assert.False(await fixture.Db.RequestLocations.AnyAsync(ct));Assert.False(await fixture.Db.RequestLocationImportStates.AnyAsync(ct));
            }
            else
            {
                await DatabaseReconciliation.ImportLocationsAsync(fixture.Db,inventory,destination,authorization,referenceHash,ct);
                var saved=await fixture.Db.RequestLocations.SingleAsync(ct);Assert.Equal(original,saved.Snapshot());
                Assert.Equal(key,saved.ImportedStorageKey);Assert.Equal(hash,saved.ImportedSha256);
                var stamp=await fixture.Db.RequestLocationImportStates.SingleAsync(ct);
                Assert.Equal(sourceHash,stamp.SourceFingerprint);Assert.Equal(referenceHash,stamp.ReferenceFingerprint);Assert.Equal(authorization,stamp.AuthorizedPlanSha256);
                await DatabaseReconciliation.ImportLocationsAsync(fixture.Db,inventory,destination,authorization,referenceHash,ct);
                Assert.Equal(1,await fixture.Db.RequestLocations.CountAsync(ct));Assert.Equal(original,saved.Snapshot());
            }
        }
        finally {Directory.Delete(root,true);}
    }
}
