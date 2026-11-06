using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Management;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Management;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class QuickIntakeDashboardTests
{
    static readonly DateTimeOffset Received = new(2026,11,5,12,0,0,TimeSpan.Zero);
    static readonly QuickIntakeRange Range = new(new(2026,11,1), new(2026,11,5));
    static QuickIntakeDashboardService Service(PostgreSqlTestDatabase db) => new(db.Db,Options.Create(new FfpManagerBridgeOptions()));
    internal static QuickIntakeRecord Intake(int index, DateTimeOffset? received=null, DateOnly? dob=null, string parish="Kingston", string employment="Employed", decimal income=10000, string gender="Male", string category="Food", string address="Fixture address") =>
        new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"PRIVATE_FIRST","PRIVATE_LAST",dob ?? new(1990,11,5),gender,"Single","PRIVATE_PHONE",null,address,"PRIVATE_COMMUNITY",parish,employment,income,false,false,"None",category,received ?? Received);

    [Fact]
    public async Task QuickOnlyInclusiveDatesAndAllDimensionsReconcileWithVerifiedIntegration()
    {
        var ct=TestContext.Current.CancellationToken;await using var db=await PostgreSqlTestDatabase.CreateAsync(ct);
        var cohort=Enumerable.Range(0,10).Select(i=>Intake(i,parish:i<5?"Saint Catherine":"Unknown place",category:i<5?"Housing":"Education",employment:i<5?"Self Employed":"Student",income:i<5?0:50000.01m,gender:i<5?"Male":"Female")).ToArray();
        db.Db.QuickIntakes.AddRange(cohort);
        db.Db.QuickIntakes.AddRange(Intake(10,Range.StartUtc.AddTicks(-1)),Intake(11,Range.EndUtc));
        // Normal requests do not count as Quick Intake, even with integration records.
        var normal=new AssistanceRequest(Guid.NewGuid(),"RP-NORMAL",Guid.NewGuid(),Guid.NewGuid(),Received);
        db.Db.AssistanceRequests.Add(normal);
        foreach(var q in cohort.Take(3)) { var link=new IntegrationLink(Guid.NewGuid(),q.RequestId,"FFPManager",Received);link.RecordVerifiedDestination("external","FFP-123","fixture","fixture",Received);link.MarkSynchronized("external",Received,"FFP-123");db.Db.IntegrationLinks.Add(link); }
        var queued=new IntegrationLink(Guid.NewGuid(),cohort[3].RequestId,"FFPManager",Received);queued.MarkQueued();db.Db.IntegrationLinks.Add(queued);
        var legacySync=new IntegrationLink(Guid.NewGuid(),cohort[4].RequestId,"FFPManager",Received);legacySync.MarkSynchronized("external",Received,"FFP-123");db.Db.IntegrationLinks.Add(legacySync);
        var other=new IntegrationLink(Guid.NewGuid(),cohort[5].RequestId,"Other",Received);other.RecordVerifiedDestination("external","FFP-123","fixture","fixture",Received);other.MarkSynchronized("external",Received,"FFP-123");db.Db.IntegrationLinks.Add(other);
        await db.Db.SaveChangesAsync(ct);
        var result=await Service(db).GetAsync(Range,ct);
        Assert.Equal(10,result.Total);Assert.Equal(3,result.SentToFfpManager);
        Assert.Equal(15,result.Parishes.Count);Assert.Equal(5,result.Parishes.Single(c=>c.Name=="St. Catherine").Count);Assert.Equal(5,result.Parishes.Last().Count);
        Assert.Equal(5,result.RequestTypes.Single(c=>c.Name=="Housing").Count);Assert.Equal(50,result.RequestTypes.Single(c=>c.Name=="Education").Percent);
        foreach(var rows in new[]{result.Parishes,result.RequestTypes,result.Employment.Items,result.Income.Items,result.Gender.Items,result.Ages.Items}) {Assert.Equal(10,rows.Sum(c=>c.Count));Assert.Equal(100,rows.Sum(c=>c.Percent));}
        Assert.False(result.Employment.Suppressed);Assert.Equal(5,result.Employment.Items.Single(c=>c.Name=="Self Employed").Count);
        Assert.Equal(5,result.Income.Items.Single(c=>c.Name=="No income").Count);Assert.Equal(5,result.Income.Items.Single(c=>c.Name=="50,000.01–100,000").Count);
        Assert.Null(result.AwaitingScreening);Assert.Null(result.FollowUpRequired);Assert.Null(result.ScreeningComplete);Assert.Null(result.NewApplicants);Assert.False(result.ChildrenUnder18.Suppressed);Assert.Equal(10,result.ChildrenUnder18.Items.Single(c=>c.Name=="No").Count);
        string json=JsonSerializer.Serialize(result);foreach(var privateValue in new[]{"PRIVATE_FIRST","PRIVATE_LAST","PRIVATE_PHONE","PRIVATE_COMMUNITY","external","FFP-123"}) Assert.DoesNotContain(privateValue,json);
        Assert.Contains(result.Insights,s=>s.Contains("50%")&&s.Contains("housing"));Assert.Contains(result.Insights,s=>s.Contains("50%")&&s.Contains("no income"));
    }

    [Fact]
    public async Task ChildrenAtHomeUsesCapturedBooleanAndDoesNotTreatLegacyDefaultAsNo()
    {
        var ct=TestContext.Current.CancellationToken;
        await using var db=await PostgreSqlTestDatabase.CreateAsync(ct);
        var yes=Enumerable.Range(0,5).Select(i=>new QuickIntakeRecord(
            Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),
            "PRIVATE_FIRST","PRIVATE_LAST",new(1992,1,1),"Female","Single",
            "PRIVATE_PHONE",null,"Fixture address","PRIVATE_COMMUNITY","Kingston",
            "Employed",10000,true,false,"None","Food",Received));
        var no=Enumerable.Range(0,5).Select(i=>Intake(i));
        db.Db.QuickIntakes.AddRange(yes.Concat(no));
        await db.Db.SaveChangesAsync(ct);
        var result=await Service(db).GetAsync(Range,ct);
        Assert.False(result.ChildrenUnder18.Suppressed);
        Assert.Equal(5,result.ChildrenUnder18.Items.Single(c=>c.Name=="Yes").Count);
        Assert.Equal(5,result.ChildrenUnder18.Items.Single(c=>c.Name=="No").Count);
        Assert.Equal(0,result.ChildrenUnder18.Items.Single(c=>c.Name=="Not provided / unknown").Count);
    }
    [Theory]
    [InlineData("2001-11-06","< 25")]
    [InlineData("2001-11-05","25–34")]
    [InlineData("1991-11-05","35–44")]
    [InlineData("1981-11-05","45–54")]
    [InlineData("1971-11-05","55–64")]
    [InlineData("1961-11-05","65+")]
    [InlineData("2030-01-01","Not provided / unknown")]
    [InlineData("1900-01-01","Not provided / unknown")]
    public async Task AgeUsesBirthdayAtSubmissionNotCurrentAge(string birth,string expected)
    {
        var ct=TestContext.Current.CancellationToken;await using var db=await PostgreSqlTestDatabase.CreateAsync(ct);
        db.Db.QuickIntakes.AddRange(Enumerable.Range(0,5).Select(i=>Intake(i,dob:DateOnly.Parse(birth))));await db.Db.SaveChangesAsync(ct);
        var result=await Service(db).GetAsync(Range,ct);Assert.Equal(5,result.Ages.Items.Single(c=>c.Name==expected).Count);
    }
    [Fact]
    public async Task SmallGroupsRemainVisibleInAuthorizedDashboardAggregates()
    {
        var ct=TestContext.Current.CancellationToken;await using var db=await PostgreSqlTestDatabase.CreateAsync(ct);
        db.Db.QuickIntakes.AddRange(Enumerable.Range(0,6).Select(i=>Intake(i,gender:i==0?"Female":"Male",employment:i==0?"Student":"Employed",income:i==0?0:50000,dob:i==0?new(2000,1,1):new(1990,1,1))));await db.Db.SaveChangesAsync(ct);
        var result=await Service(db).GetAsync(Range,ct);
        foreach(var dimension in new[]{result.Gender,result.Employment,result.Income,result.Ages}) {Assert.False(dimension.Suppressed);Assert.Equal(6,dimension.Items.Sum(c=>c.Count));}
        Assert.Equal(1,result.Gender.Items.Single(c=>c.Name=="Female").Count);
        Assert.Equal(1,result.Employment.Items.Single(c=>c.Name=="Student").Count);
        Assert.Equal(1,result.Income.Items.Single(c=>c.Name=="No income").Count);
        Assert.Equal(1,result.Ages.Items.Single(c=>c.Name=="25–34").Count);
    }
    [Fact]
    public async Task LegacyBackfillDefaultsDoNotBecomeFoodUnemployedOrNoIncomeStatistics()
    {
        var ct=TestContext.Current.CancellationToken;await using var db=await PostgreSqlTestDatabase.CreateAsync(ct);
        db.Db.QuickIntakes.AddRange(Enumerable.Range(0,5).Select(i=>Intake(i,dob:new(1900,1,1),address:"Not captured",employment:"Unemployed",income:0,gender:"Non-Binary")));await db.Db.SaveChangesAsync(ct);
        var result=await Service(db).GetAsync(Range,ct);
        foreach(var rows in new[]{result.RequestTypes,result.Gender.Items,result.Employment.Items,result.Income.Items,result.Ages.Items}) Assert.Equal(5,rows.Single(c=>c.Name=="Not provided / unknown").Count);
        Assert.Equal(5,result.ChildrenUnder18.Items.Single(c=>c.Name=="Not provided / unknown").Count);
        Assert.DoesNotContain(result.Insights,s=>s.Contains("no income")||s.Contains("food assistance"));
    }
    [Fact]
    public async Task EmptyCohortHasZeroCountsExplicitUnavailableMetricsAndNoInventedInsights()
    {
        var ct=TestContext.Current.CancellationToken;await using var db=await PostgreSqlTestDatabase.CreateAsync(ct);
        var result=await Service(db).GetAsync(Range,ct);Assert.Equal(0,result.Total);Assert.Equal(0,result.SentToFfpManager);Assert.All(result.Parishes,c=>Assert.Equal(0,c.Count));Assert.Contains("No Quick Intakes",Assert.Single(result.Insights));
        await Assert.ThrowsAsync<ArgumentException>(()=>Service(db).GetAsync(new(new(2026,11,6),new(2026,11,5)),ct));
    }
    [Fact]
    public async Task DrilldownUsesIdenticalDateAndParishFiltersAndOnlyQuickIntakeReferences()
    {
        var ct=TestContext.Current.CancellationToken;await using var db=await PostgreSqlTestDatabase.CreateAsync(ct);
        var q=Intake(0,parish:"Saint Mary");db.Db.QuickIntakes.Add(q);db.Db.AssistanceRequests.Add(new(q.RequestId,"RP-QUICK",q.IndividualContextId,q.AnonymousUserId,Received));
        db.Db.AssistanceRequests.Add(new(Guid.NewGuid(),"RP-NORMAL",q.IndividualContextId,q.AnonymousUserId,Received));await db.Db.SaveChangesAsync(ct);
        Assert.Equal("RP-QUICK",Assert.Single(await Service(db).GetRecordsAsync(Range,"St. Mary",1,ct)).Reference);
        Assert.Empty(await Service(db).GetRecordsAsync(Range,"Kingston",1,ct));Assert.Empty(await Service(db).GetRecordsAsync(new(new(2026,11,1),new(2026,11,4)),null,1,ct));
    }
}
