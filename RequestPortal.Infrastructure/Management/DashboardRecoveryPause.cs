using Microsoft.EntityFrameworkCore;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Management;

public sealed record DashboardPauseState(long SnapshotVersion, int PreviousAttemptCount, DateTimeOffset PreviousNextRefreshUtc,
    int PausedAttemptCount, DateTimeOffset PausedUntilUtc);

/// <summary>Operator-only pause of the known throttling loop; never changes readiness or snapshot contents.</summary>
public static class DashboardRecoveryPause
{
    private static Task LockAsync(RequestPortalDbContext db, CancellationToken ct) => db.Database.ExecuteSqlRawAsync(
        "SET LOCAL lock_timeout='30s'; SET LOCAL statement_timeout='45s'; SELECT pg_advisory_xact_lock(7294321651::bigint)", ct);

    public static async Task<DashboardPauseState> PauseAsync(RequestPortalDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        var row = await db.ManagementDashboardAggregates.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        var until = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
        int attempts = Math.Max(1, row.AttemptCount);
        await db.ManagementDashboardAggregates.Where(x => x.Id == 1).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.AttemptCount, attempts).SetProperty(x => x.NextRefreshUtc, until), ct);
        await tx.CommitAsync(ct);
        return new(row.SnapshotVersion, row.AttemptCount, row.NextRefreshUtc, attempts, until);
    }

    public static async Task<bool> ResumeAsync(RequestPortalDbContext db, DashboardPauseState state, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(db, ct);
        int changed = await db.ManagementDashboardAggregates.Where(x => x.Id == 1 && x.SnapshotVersion == state.SnapshotVersion &&
            x.NextRefreshUtc == state.PausedUntilUtc && x.AttemptCount == state.PausedAttemptCount)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.AttemptCount, state.PreviousAttemptCount)
                .SetProperty(x => x.NextRefreshUtc, state.PreviousNextRefreshUtc), ct);
        await tx.CommitAsync(ct);
        return changed == 1;
    }
}
