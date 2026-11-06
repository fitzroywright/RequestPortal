using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Web.Pages.Management;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class RegisteredUsersDashboardTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public async Task PostgreSqlCohortExcludesQuickOnlyAccountsAndSubmissionsWithoutDuplicatingRepresentatives()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await PostgreSqlTestDatabase.CreateAsync(ct);
        var db = fixture.Db;
        ApplicationUser User(string name, string? parish = "Kingston", int age = 1) => new()
        {
            Id = Guid.NewGuid(), UserName = name, CreatedUtc = Now.AddDays(-age),
            Parish = parish, EmailConfirmed = true, LastSignInUtc = Now
        };
        var individual = User("registered-individual");
        var representative = User("registered-representative", "Saint Catherine");
        var unknown = User("registered-unknown", "Unknown parish");
        unknown.EmailConfirmed = false;
        representative.LockoutEnd = Now.AddDays(1);
        var placeholder = User("quick-fixture@requestportal.local");
        var tracking = User("legacy-tracking");
        var staff = User("staff"); staff.IsInternalStaff = true;
        var old = User("old-registration", age: 31);
        var future = User("future-registration", age: -1);
        db.Users.AddRange(individual, representative, unknown, placeholder, tracking, staff, old, future);
        var org = new Organization(Guid.NewGuid(), "Fixture organization", "JM", representative.Id, Now);
        var second = new Organization(Guid.NewGuid(), "Second fixture organization", "JM", representative.Id, Now);
        db.Organizations.AddRange(org, second);
        foreach (var organization in new[] { org, second })
        {
            var member = new OrganizationMembership(Guid.NewGuid(), organization.Id, representative.Id, Now);
            member.Approve(staff.Id, Now); db.OrganizationMemberships.Add(member);
        }
        db.OrganizationMemberships.Add(new(Guid.NewGuid(), org.Id, individual.Id, Now));
        AssistanceRequest Request(ApplicationUser user, bool quick = false, bool converted = false)
        {
            var request = new AssistanceRequest(Guid.NewGuid(), "TEST-" + Guid.NewGuid().ToString("N"), org.Id, user.Id, Now);
            request.SetKind(RequestKind.Project); request.Submit(user.Id, Now);
            db.AssistanceRequests.Add(request);
            if (quick)
            {
                var intake = new QuickIntakeRecord(Guid.NewGuid(), request.Id, user.Id, org.Id,
                    "Private", "Fixture", new(1990, 1, 1), "Female", "Single", "5550000000", null,
                    "Fixture address", "Fixture community", "Kingston", "Employed", 10000, false, false, "None", "Food", Now);
                if (converted) intake.MarkConverted(individual.Id, staff.Id, Now);
                db.QuickIntakes.Add(intake);
            }
            return request;
        }
        Request(individual); Request(representative);
        Request(placeholder, quick: true); Request(tracking, quick: true);
        Request(individual, quick: true, converted: true);
        Request(staff); Request(old);
        await db.SaveChangesAsync(ct);
        var page = new RegisteredUsersModel(db, new Clock());
        Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(await page.OnGetAsync(ct));
        var snapshot = page.Dashboard;
        Assert.Equal(3, snapshot.Total); Assert.Equal(2, snapshot.Individuals);
        Assert.Equal(1, snapshot.Representatives); Assert.Equal(2, snapshot.Organizations);
        Assert.Equal(1, snapshot.PendingMemberships); Assert.Equal(3, snapshot.RecentlyActive);
        Assert.Equal(2, snapshot.RegularSubmissions); Assert.Equal(1, snapshot.UnknownParish);
        Assert.Equal(1, snapshot.Parishes.Single(p => p.Name == "St. Catherine").Count);
        Assert.Equal(3, snapshot.Trend.Sum(p => p.Count));
        Assert.All(snapshot.AccountStates, state => Assert.Equal(1, state.Count));
        Assert.False(db.ChangeTracker.HasChanges());

        await using var host = await QuickIntakeDashboardPageTests.Fixture.Create(db: db);
        using var response = await host.Client.GetAsync("/Management/RegisteredUsers", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        string html = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("Registered Users Dashboard", html);
        Assert.Contains("registered-users-dashboard.js", html);
        Assert.DoesNotContain("registered-individual", html);
        string? output = Environment.GetEnvironmentVariable("REQUESTPORTAL_RU_RENDER_OUTPUT");
        if (output is not null) { Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, "index.html"), html, ct); }
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("public", HttpStatusCode.Forbidden)]
    [InlineData("no-management", HttpStatusCode.Forbidden)]
    public async Task RegisteredUsersRequiresExistingManagementPermission(string actor, HttpStatusCode expected)
    {
        await using var host = await QuickIntakeDashboardPageTests.Fixture.Create(actor);
        using var response = await host.Client.GetAsync("/Management/RegisteredUsers", TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("invalid", null, null)]
    [InlineData("custom", null, null)]
    [InlineData("custom", "2026-11-05", "2026-11-01")]
    [InlineData("custom", "2026-11-01", "2026-11-06")]
    [InlineData("custom", "2025-01-01", "2026-11-05")]
    public async Task InvalidRangesAreRejectedBeforeQuerying(string period, string? from, string? through)
    {
        var page = new RegisteredUsersModel(null!, new Clock())
        {
            Period = period, From = from is null ? null : DateOnly.Parse(from),
            Through = through is null ? null : DateOnly.Parse(through)
        };
        Assert.IsType<BadRequestObjectResult>(await page.OnGetAsync(TestContext.Current.CancellationToken));
    }
}
