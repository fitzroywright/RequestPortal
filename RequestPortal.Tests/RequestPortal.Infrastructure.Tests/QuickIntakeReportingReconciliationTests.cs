using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Management;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Management;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class QuickIntakeReportingReconciliationTests
{
    [Fact]
    public void PagingAndDetailRoutesPreserveReportingScope()
    {
        var model = new RequestPortal.Web.Pages.Staff.IntegrationsModel(null!, null!, null!, null!, null!, null!)
        {
            Source = "QuickIntake", NotVerified = true, Search = "example", Status = "Failed",
            From = new(2026, 10, 1), Through = new(2026, 10, 31), Sort = "oldest"
        };
        var routes = model.Routes(2);
        Assert.Equal("QuickIntake", routes["Source"]);
        Assert.Equal("True", routes["NotVerified"]);
        Assert.Equal("2026-10-01", routes["From"]);
        Assert.Equal("2026-10-31", routes["Through"]);
        Assert.Equal("oldest", routes["Sort"]);
        Assert.Equal("example", routes["Search"]);
        Assert.Equal("Failed", routes["Status"]);
        Assert.Equal("2", routes["PageNumber"]);
        Assert.Equal("QuickIntake", model.DetailRoutes(Guid.NewGuid())["Source"]);
    }

    [Fact]
    public async Task DashboardAndMonitorReconcileExactIdsUtcBoundariesSourcesAndPagesWithoutMutations()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(ct);
        var db = database.Db;
        var range = new QuickIntakeRange(new(2026, 10, 1), new(2026, 10, 31));
        var intakes = Enumerable.Range(0, 33).Select(i => QuickIntakeDashboardTests.Intake(i,
            i == 0 ? range.StartUtc.AddMilliseconds(-1) : i == 32 ? range.EndUtc : i == 31 ? range.EndUtc.AddMilliseconds(-1) : range.StartUtc.AddHours(i - 1))).ToArray();
        db.QuickIntakes.AddRange(intakes);
        foreach (var q in intakes)
        {
            // Deliberately different request dates; reporting must use the intake date.
            var request = new AssistanceRequest(q.RequestId, "RP-" + Array.IndexOf(intakes, q).ToString("D3"), q.IndividualContextId, q.AnonymousUserId, range.StartUtc.AddDays(-10));
            db.AssistanceRequests.Add(request);
        }
        var normal = new AssistanceRequest(Guid.NewGuid(), "RP-NORMAL", Guid.NewGuid(), Guid.NewGuid(), range.StartUtc);
        // A regular submitted request must stay outside the Quick Intake population.
        typeof(AssistanceRequest).GetProperty("Status")!.SetValue(normal, RequestStatus.Submitted);
        db.AssistanceRequests.Add(normal);
        foreach (var i in Enumerable.Range(1, 6))
        {
            var link = new IntegrationLink(Guid.NewGuid(), intakes[i].RequestId, "FFPManager", range.StartUtc);
            link.MarkSynchronized("destination", range.StartUtc, "APP");
            link.RecordVerifiedDestination("destination", "APP", "test", "test", range.StartUtc);
            if (i > 1)
            {
                string property = i switch { 2 => "VerifiedUtc", 3 => "SynchronizedUtc", 4 => "ExternalReference", 5 => "ApplicationNumber", _ => "Status" };
                typeof(IntegrationLink).GetProperty(property)!.SetValue(link, i == 6 ? SynchronizationStatus.Failed : null);
            }
            db.IntegrationLinks.Add(link);
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var options = Options.Create(new FfpManagerBridgeOptions());
        var dashboard = await new QuickIntakeDashboardService(db, options).GetAsync(range, ct);
        var monitor = new IntegrationStatusService(db, new SystemClock(), null!, options, null!, null!, null!);
        var query = new IntegrationMonitorQuery(FromUtc: range.StartUtc, ThroughUtc: range.EndUtc,
            Source: "QuickIntake", NotVerified: true, Sort: "oldest");
        var first = await monitor.SearchAsync(query, ct);
        var second = await monitor.SearchAsync(query with { Page = 2 }, ct);
        var ids = first.Items.Concat(second.Items).Select(r => r.RequestId).ToArray();
        var expected = intakes.Skip(2).Take(30).Select(q => q.RequestId).ToArray();
        Assert.Equal(31, dashboard.Total);
        Assert.Equal(1, dashboard.SentToFfpManager);
        Assert.Equal(dashboard.Total, dashboard.SentToFfpManager + dashboard.NotVerifiedInFfpManager);
        Assert.Equal(dashboard.NotVerifiedInFfpManager, first.TotalCount);
        Assert.Equal(expected.Order(), ids.Order());
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.Equal(first.TotalCount, first.NotVerifiedBreakdown.Values.Sum());
        Assert.Equal(25, first.NotVerifiedBreakdown["Missing Integration Link"]);
        Assert.All(first.Items, row => Assert.Equal("Quick Intake", row.Source));
        Assert.Equal(intakes[2].RequestId, first.Items[0].RequestId);
        var search = await monitor.SearchAsync(query with { Search = "RP-002" }, ct);
        Assert.Equal(intakes[2].RequestId, Assert.Single(search.Items).RequestId);
        var status = await monitor.SearchAsync(query with { Status = "ReconciliationRequired" }, ct);
        Assert.All(status.Items, row => Assert.Equal(SynchronizationStatus.ReconciliationRequired, row.Status));
        Assert.Equal(normal.Id, Assert.Single((await monitor.SearchAsync(query with { Source = "RequestPortal", NotVerified = false }, ct)).Items).RequestId);
        Assert.Equal(normal.Id, Assert.Single((await monitor.SearchAsync(new(), ct)).Items).RequestId); // existing All Sources draft exclusion
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
