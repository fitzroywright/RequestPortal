using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Management;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Management;

public sealed class QuickIntakeDashboardService(RequestPortalDbContext db, IOptions<FfpManagerBridgeOptions> integration) : IQuickIntakeDashboardService
{
    private const string Missing = "Not provided / unknown";
    private static readonly string[] Types = ["Food", "Housing", "Earn an Income", "Education", "Health", "Water & Hygiene"];
    private static readonly string[] Jobs = ["Employed", "Self Employed", "Unemployed", "Student"];
    private static readonly string[] Genders = ["Male", "Female", "Prefer not to Say"];
    private static readonly string[] AgeLabels = ["< 25", "25–34", "35–44", "45–54", "55–64", "65+", Missing];
    private static readonly string[] IncomeLabels = ["No income", "≤ 50,000", "50,000.01–100,000", "100,000.01–200,000", "> 200,000", Missing];

    private static readonly string[] WeeklyIncomeLabels = ["No income", "≤ 5,000", "Over 5,000 to 10,000", "Over 10,000 to 20,000", "> 20,000", Missing];

    private IQueryable<QuickIntakeRecord> Cohort(QuickIntakeRange range)
    {
        range.Validate();
        return QuickIntakeReporting.Population(db, range.StartUtc, range.EndUtc);
    }
    public async Task<QuickIntakeDashboardSnapshot> GetAsync(QuickIntakeRange range, CancellationToken ct = default, string? parish = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var query = Cohort(range);
        if (!string.IsNullOrWhiteSpace(parish))
        {
            var normalized = JamaicaParishes.Normalize(parish);
            if (!JamaicaParishes.Names.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a valid parish.");
            var matching = await query.Select(q => q.Parish).Distinct().ToArrayAsync(ct);
            var names = matching.Where(name => JamaicaParishes.Normalize(name) == normalized).ToArray();
            query = query.Where(q => names.Contains(q.Parish));
        }
        int total = await query.CountAsync(ct);
        var verifiedIds = QuickIntakeReporting.VerifiedRequestIds(db, integration.Value.SystemName);
        int sent = await query.CountAsync(q => verifiedIds.Contains(q.RequestId), ct);
        // Only grouped projections leave PostgreSQL. No applicant identities, DOBs,
        // contact details or individual demographic records are materialized.
        var parishRows = await query.GroupBy(q => q.Parish).Select(g => new RawCount(g.Key, g.Count())).ToArrayAsync(ct);
        var types = await query.GroupBy(q => q.DateOfBirth == new DateOnly(1900, 1, 1) && q.StreetAddress == "Not captured" ? Missing : q.HelpCategory).Select(g => new RawCount(g.Key, g.Count())).ToArrayAsync(ct);
        // Older records were backfilled with false without an actual answer.
        // The paired DOB/address sentinel distinguishes those unknown responses.
        var childRows = await query.GroupBy(q => q.DateOfBirth == new DateOnly(1900, 1, 1) && q.StreetAddress == "Not captured"
            ? Missing : q.HasChildrenUnder18LivingFullTime ? "Yes" : "No")
            .Select(g => new RawCount(g.Key, g.Count())).ToArrayAsync(ct);
        var jobs = await query.GroupBy(q => q.DateOfBirth == new DateOnly(1900, 1, 1) && q.StreetAddress == "Not captured" ? Missing : q.EmploymentStatus).Select(g => new RawCount(g.Key, g.Count())).ToArrayAsync(ct);
        var genders = await query.GroupBy(q => q.DateOfBirth == new DateOnly(1900, 1, 1) && q.StreetAddress == "Not captured" ? Missing : q.Gender).Select(g => new RawCount(g.Key, g.Count())).ToArrayAsync(ct);
        // The legacy MonthlyIncome property stores weekly JMD; convert each amount before monthly classification.
        var income = await query.Select(q => new { q.DateOfBirth, q.StreetAddress, Income = q.MonthlyIncome * 52m / 12m })
            .GroupBy(q => (q.DateOfBirth == new DateOnly(1900, 1, 1) && q.StreetAddress == "Not captured") || q.Income < 0 ? Missing : q.Income == 0 ? IncomeLabels[0] :
                q.Income <= 50000 ? IncomeLabels[1] : q.Income <= 100000 ? IncomeLabels[2] : q.Income <= 200000 ? IncomeLabels[3] : IncomeLabels[4])
            .Select(g => new RawCount(g.Key, g.Count())).ToArrayAsync(ct);
        var weeklyIncome = await query.Select(q => new { q.DateOfBirth, q.StreetAddress, Income = q.MonthlyIncome })
            .GroupBy(q => (q.DateOfBirth == new DateOnly(1900, 1, 1) && q.StreetAddress == "Not captured") || q.Income < 0 ? Missing : q.Income == 0 ? WeeklyIncomeLabels[0] :
                q.Income <= 5000 ? WeeklyIncomeLabels[1] : q.Income <= 10000 ? WeeklyIncomeLabels[2] : q.Income <= 20000 ? WeeklyIncomeLabels[3] : WeeklyIncomeLabels[4])
            .Select(g => new RawCount(g.Key, g.Count())).ToArrayAsync(ct);
        var ages = await query.Select(q => new { Age = q.CreatedUtc.Year - q.DateOfBirth.Year -
            (q.CreatedUtc.Month < q.DateOfBirth.Month || q.CreatedUtc.Month == q.DateOfBirth.Month && q.CreatedUtc.Day < q.DateOfBirth.Day ? 1 : 0), q.DateOfBirth })
            .GroupBy(q => q.DateOfBirth == DateOnly.MinValue || q.DateOfBirth == new DateOnly(1900, 1, 1) || q.Age < 0 || q.Age > 120 ? Missing : q.Age < 25 ? AgeLabels[0] :
                q.Age < 35 ? AgeLabels[1] : q.Age < 45 ? AgeLabels[2] : q.Age < 55 ? AgeLabels[3] : q.Age < 65 ? AgeLabels[4] : AgeLabels[5])
            .Select(g => new RawCount(g.Key, g.Count())).ToArrayAsync(ct);
        var parishes = Counts(parishRows, JamaicaParishes.Names.Append(JamaicaParishes.Unknown).ToArray(), JamaicaParishes.Normalize, total);
        var requestTypes = Counts(types, Types.Append(Missing).ToArray(), name => Canonical(name, Types), total);
        var children = Protect(Counts(childRows, ["Yes", "No", Missing], name => name, total));
        var employment = Protect(Counts(jobs, Jobs.Append(Missing).ToArray(), name => Canonical(name, Jobs), total));
        var gender = Protect(Counts(genders, Genders.Append(Missing).ToArray(), name => Canonical(name, Genders), total));
        var incomes = Protect(Counts(income, IncomeLabels, name => name, total));
        var ageGroups = Protect(Counts(ages, AgeLabels, name => name, total));
        List<string> insights = [];
        if (total == 0) insights.Add("No Quick Intakes were received in this date range.");
        else
        {
            var leading = requestTypes.Where(c => c.Name != Missing).OrderByDescending(c => c.Count).First();
            if (leading.Count > 0) insights.Add($"{leading.Percent:0.#}% of Quick Intakes request {leading.Name.ToLowerInvariant()} assistance.");
            AddLargest(insights, gender, "Gender", [Missing, "Prefer not to Say"]);
            AddLargest(insights, ageGroups, "Age at intake", [Missing]);
            if (!incomes.Suppressed)
            {
                var noIncome = incomes.Items[0];
                if (noIncome.Count > 0) insights.Add($"{noIncome.Percent:0.#}% report no income.");
            }
            if (insights.Count == 0) insights.Add("No sufficiently complete statistical observations are available.");
        }
        await transaction.CommitAsync(ct);
        return new(range, total, sent, parishes, requestTypes,
            children,
            employment, incomes, gender, ageGroups, insights)
        { WeeklyIncome = Protect(Counts(weeklyIncome, WeeklyIncomeLabels, name => name, total)) };
    }
    public async Task<IReadOnlyList<QuickIntakeRecordLink>> GetRecordsAsync(QuickIntakeRange range, string? parish, int page, CancellationToken ct = default)
    {
        var query = Cohort(range);
        // Use the same normalization as the map without loading individual rows.
        if (!string.IsNullOrWhiteSpace(parish))
        {
            string normalized = JamaicaParishes.Normalize(parish);
            var raw = await query.Select(q => q.Parish).Distinct().ToArrayAsync(ct);
            string[] matching = raw.Where(p => JamaicaParishes.Normalize(p) == normalized).ToArray();
            query = query.Where(q => matching.Contains(q.Parish));
        }
        return await (from q in query join r in db.AssistanceRequests.AsNoTracking() on q.RequestId equals r.Id
            orderby q.CreatedUtc descending, q.RequestId
            select new QuickIntakeRecordLink(q.RequestId, r.PublicReference, q.Parish, q.CreatedUtc))
            .Skip((Math.Clamp(page, 1, 10000) - 1) * 50).Take(50).ToArrayAsync(ct);
    }
    private static string Canonical(string value, string[] allowed) => allowed.FirstOrDefault(n => string.Equals(n, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Missing;
    private static QuickIntakeCount[] Counts(RawCount[] source, string[] labels, Func<string, string> normalize, int total)
    {
        var grouped = source.GroupBy(r => normalize(r.Name)).ToDictionary(g => g.Key, g => g.Sum(r => r.Count));
        return labels.Select(name => new QuickIntakeCount(name, grouped.GetValueOrDefault(name), total == 0 ? 0 : Math.Round(grouped.GetValueOrDefault(name) * 100m / total, 1))).ToArray();
    }
    private static QuickIntakeDistribution Protect(QuickIntakeCount[] items)
    {
        // Authorized staff reports show complete aggregates, including small groups.
        return new(items, false);
    }
    private static void AddLargest(List<string> insights, QuickIntakeDistribution data, string label, string[] exclude)
    {
        if (data.Suppressed) return;
        var largest = data.Items.Where(c => !exclude.Contains(c.Name)).OrderByDescending(c => c.Count).FirstOrDefault();
        if (largest?.Count > 0) insights.Add($"{label}: {largest.Name} is the largest recorded group ({largest.Percent:0.#}% of all Quick Intakes).");
    }
    private sealed record RawCount(string Name, int Count);
}
