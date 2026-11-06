using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Messages;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Messages;

// Responsibilities are application-owned Identity claims, managed only by the
// administration page. They are not roles or authentication permission grants.
public sealed class StaffNotificationRouting(RequestPortalDbContext db, IStaffNotificationAccess? access = null)
{
    public const string ClaimType = "requestportal.notification-responsibility";
    public const string Intake = "intake";
    public const string Operations = "operations";

    public async Task<Guid[]> RecipientsAsync(Guid? requestId, bool operational, CancellationToken ct, Guid? threadId = null)
    {
        Guid[] assigned = (requestId.HasValue || threadId.HasValue) && !operational
            ? await db.MessageThreads.AsNoTracking()
                .Where(t => (threadId.HasValue ? t.Id == threadId : t.RequestId == requestId && t.Audience != MessageAudience.FfpInternal) && t.AssignedToStaffUserId != null)
                .Select(t => t.AssignedToStaffUserId!.Value).Distinct().ToArrayAsync(ct)
            : [];
        // An assignment replaces the intake queue; operational escalation has its
        // own explicit responsibility and never falls back to administrators.
        Guid[] candidates = assigned.Length > 0 ? assigned : await db.UserClaims.AsNoTracking()
            .Where(c => c.ClaimType == ClaimType && c.ClaimValue == (operational ? Operations : Intake))
            .Select(c => c.UserId).Distinct().ToArrayAsync(ct);
        return await EligibleAsync(candidates, ct);
    }

    public async Task<Guid[]> EligibleAsync(IEnumerable<Guid> candidates, CancellationToken ct)
    {
        Guid[] ids = candidates.Distinct().ToArray();
        Guid[] staff = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.IsInternalStaff &&
                (u.LockoutEnd == null || u.LockoutEnd <= DateTimeOffset.UtcNow))
            .Select(u => u.Id).ToArrayAsync(ct);
        if (access is null) return staff; // Isolated infrastructure fixtures.
        List<Guid> result = [];
        foreach (Guid id in staff)
            if (await access.IsEligibleAsync(id, ct)) result.Add(id);
        return result.ToArray();
    }

    public Task<bool> IsAdministratorAsync(Guid id, CancellationToken ct) =>
        access?.IsAdministratorAsync(id, ct) ?? Task.FromResult(false);
}

public interface IStaffNotificationAccess
{
    Task<bool> IsEligibleAsync(Guid userId, CancellationToken ct);
    Task<bool> IsAdministratorAsync(Guid userId, CancellationToken ct);
}
