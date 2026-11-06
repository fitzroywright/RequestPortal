using System.Security.Claims;
using Common.Security.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Auditing;
using RequestPortal.Infrastructure.Messages;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Services;

public sealed record NotificationRoutingEntry(Guid Id, string Name, bool Eligible, bool Intake, bool Operations, string Version);

public sealed class NotificationRoutingAdministration(RequestPortalDbContext db, StaffAdministrationService administration,
    StaffNotificationRouting routing, IStaffNotificationAccess access)
{
    private async Task<Guid> RequireAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        await administration.RequireAdministratorAsync(actor);
        if (!Guid.TryParse(actor.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ||
            !await access.IsAdministratorAsync(id, ct)) throw new UnauthorizedAccessException();
        return id;
    }

    public async Task<IReadOnlyList<NotificationRoutingEntry>> ListAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        await RequireAsync(actor, ct);
        var users = await db.Users.AsNoTracking().Where(u => u.IsInternalStaff).OrderBy(u => u.DisplayName).ToArrayAsync(ct);
        var claims = await db.UserClaims.AsNoTracking().Where(c => c.ClaimType == StaffNotificationRouting.ClaimType).ToArrayAsync(ct);
        var eligible = (await routing.EligibleAsync(users.Select(u => u.Id), ct)).ToHashSet();
        return users.Select(u => {
            var values = claims.Where(c => c.UserId == u.Id).Select(c => c.ClaimValue!).ToArray();
            return new NotificationRoutingEntry(u.Id, u.DisplayName, eligible.Contains(u.Id),
                values.Contains(StaffNotificationRouting.Intake), values.Contains(StaffNotificationRouting.Operations), Version(values));
        }).ToArray();
    }

    public async Task SaveAsync(ClaimsPrincipal actor, Guid userId, bool intake, bool operations, string version, CancellationToken ct)
    {
        Guid actorId = await RequireAsync(actor, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({'N' + userId.ToString()}))", ct);
        var old = await db.UserClaims.Where(c => c.UserId == userId && c.ClaimType == StaffNotificationRouting.ClaimType).ToArrayAsync(ct);
        if (version != Version(old.Select(c => c.ClaimValue!))) throw new InvalidOperationException("Routing changed. Reload before saving.");
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.IsInternalStaff, ct) ||
            (intake || operations) && (await routing.EligibleAsync([userId], ct)).Length == 0)
            throw new InvalidOperationException("Choose an active staff member authorized to review requests.");
        db.UserClaims.RemoveRange(old);
        string[] selected = new[] { intake ? StaffNotificationRouting.Intake : null, operations ? StaffNotificationRouting.Operations : null }
            .OfType<string>().ToArray();
        foreach (string responsibility in selected)
            db.UserClaims.Add(new IdentityUserClaim<Guid> { UserId = userId, ClaimType = StaffNotificationRouting.ClaimType, ClaimValue = responsibility });
        db.AuditEntries.Add(new AuditEntry(Guid.NewGuid(), actorId, "NotificationRoutingChanged", "StaffNotificationRouting", userId, null,
            DateTimeOffset.UtcNow, $"previous={Version(old.Select(c => c.ClaimValue!))}; current={Version(selected)}"));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static string Version(IEnumerable<string> values) => string.Join(',', values.Order(StringComparer.Ordinal));
}
