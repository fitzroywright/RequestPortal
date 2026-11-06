using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Management;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Pages.Management;

// Reporting aggregates only. Synthetic Quick Intake identities are excluded,
// but a genuinely registered user remains counted even if they later submit a Quick Intake.
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class RegisteredUsersModel(RequestPortalDbContext db, TimeProvider clock) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Period { get; set; } = "30";
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Through { get; set; }
    public RegisteredUsersSnapshot Dashboard { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var range = Period switch
        {
            "7" => (today.AddDays(-6), today),
            "30" => (today.AddDays(-29), today),
            "90" => (today.AddDays(-89), today),
            "custom" when From.HasValue && Through.HasValue => (From.Value, Through.Value),
            _ => (DateOnly.MinValue, DateOnly.MinValue)
        };
        if (range.Item1 == DateOnly.MinValue || range.Item2 < range.Item1 ||
            range.Item2 > today || range.Item2.DayNumber - range.Item1.DayNumber > 365)
            return BadRequest("Select a valid reporting period (up to 366 days).");

        From = range.Item1; Through = range.Item2;
        var start = new DateTimeOffset(From.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(Through.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        DateTimeOffset now = clock.GetUtcNow();

        IQueryable<ApplicationUser> accounts = db.Users.AsNoTracking()
            .Where(u => !u.IsInternalStaff && u.CreatedUtc >= start && u.CreatedUtc < end &&
                (u.UserName == null || !u.UserName.StartsWith("quick-") ||
                 !u.UserName.EndsWith("@requestportal.local")) &&
                !db.QuickIntakes.Any(q => q.AnonymousUserId == u.Id && q.ConvertedToUserId == null));

        // The user count is an account count, never a count of Quick Intake requests
        // or of organizational memberships (which can be many-to-many).
        int total = await accounts.CountAsync(ct);
        var organizationMembers = accounts.Where(u => db.OrganizationMemberships.Any(m =>
            m.UserId == u.Id && m.Status == MembershipStatus.Approved &&
            db.Organizations.Any(o => o.Id == m.OrganizationId && o.OrganizationType != "IndividualContext")));
        int representatives = await organizationMembers.CountAsync(ct);
        int individuals = total - representatives;
        int recentlyActive = await accounts.CountAsync(u => u.LastSignInUtc >= now.AddDays(-30), ct);
        int pendingMemberships = await db.OrganizationMemberships.AsNoTracking()
            .CountAsync(m => m.Status == MembershipStatus.Pending &&
                accounts.Any(u => u.Id == m.UserId) &&
                db.Organizations.Any(o => o.Id == m.OrganizationId && o.OrganizationType != "IndividualContext"), ct);
        int organizations = await db.Organizations.AsNoTracking()
            .CountAsync(o => o.OrganizationType != "IndividualContext" &&
                db.OrganizationMemberships.Any(m => m.OrganizationId == o.Id && m.Status == MembershipStatus.Approved &&
                    accounts.Any(u => u.Id == m.UserId)), ct);

        // Count real regular submissions only. A registered applicant can also
        // have Quick Intakes; those submissions must never inflate this figure.
        int regularSubmissions = await db.AssistanceRequests.AsNoTracking()
            .CountAsync(r => r.SubmittedUtc != null && r.SubmittedUtc >= start && r.SubmittedUtc < end &&
                accounts.Any(u => u.Id == r.CreatedByUserId) &&
                !db.QuickIntakes.Any(q => q.RequestId == r.Id), ct);

        var parishGroups = await accounts.GroupBy(u => u.Parish)
            .Select(g => new { Name = g.Key, Count = g.Count() }).ToArrayAsync(ct);
        Dictionary<string, int> parishes = JamaicaParishes.Names
            .ToDictionary(name => name, _ => 0, StringComparer.OrdinalIgnoreCase);
        int unknown = 0;
        foreach (var group in parishGroups)
        {
            string parish = JamaicaParishes.Normalize(group.Name ?? "");
            if (parishes.ContainsKey(parish)) parishes[parish] += group.Count;
            else unknown += group.Count;
        }

        var daily = await accounts.GroupBy(u => u.CreatedUtc.Date)
            .Select(g => new { Day = g.Key, Count = g.Count() }).ToArrayAsync(ct);
        var countsByDate = daily.ToDictionary(x => DateOnly.FromDateTime(x.Day), x => x.Count);
        var trend = new List<RegisteredUserTrend>();
        // At most eight display points; group the selected period into uniform buckets.
        int days = Through.Value.DayNumber - From.Value.DayNumber + 1;
        int bucketDays = Math.Max(1, (int)Math.Ceiling(days / 8d));
        for (int offset = 0; offset < days; offset += bucketDays)
        {
            int length = Math.Min(bucketDays, days - offset);
            DateOnly first = From.Value.AddDays(offset);
            int count = Enumerable.Range(0, length)
                .Sum(delta => countsByDate.GetValueOrDefault(first.AddDays(delta)));
            trend.Add(new RegisteredUserTrend(first.ToString("dd MMM"), count));
        }

        int locked = await accounts.CountAsync(u => u.LockoutEnd != null && u.LockoutEnd > now, ct);
        int confirmed = await accounts.CountAsync(u =>
            !(u.LockoutEnd != null && u.LockoutEnd > now) && u.EmailConfirmed, ct);
        int unconfirmed = total - locked - confirmed;

        Dashboard = new RegisteredUsersSnapshot(
            From.Value, Through.Value, total, individuals, representatives, recentlyActive,
            pendingMemberships, organizations, regularSubmissions,
            parishes.Select(p => new RegisteredUserCount(p.Key, p.Value)).ToArray(),
            unknown, trend,
            [
                new("Email confirmed", confirmed),
                new("Email not confirmed", unconfirmed),
                new("Temporarily locked", locked)
            ]);
        return Page();
    }
}

public sealed record RegisteredUserCount(string Name, int Count);
public sealed record RegisteredUserTrend(string Label, int Count);
public sealed record RegisteredUsersSnapshot(
    DateOnly From, DateOnly Through, int Total, int Individuals, int Representatives,
    int RecentlyActive, int PendingMemberships, int Organizations, int RegularSubmissions,
    IReadOnlyList<RegisteredUserCount> Parishes, int UnknownParish,
    IReadOnlyList<RegisteredUserTrend> Trend, IReadOnlyList<RegisteredUserCount> AccountStates);
