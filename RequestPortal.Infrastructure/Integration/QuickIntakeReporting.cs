using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Integration;

// Read-only reporting definitions. Dates are intake creation dates, with an exclusive UTC end.
internal static class QuickIntakeReporting
{
    internal static IQueryable<QuickIntakeRecord> Population(RequestPortalDbContext db,
        DateTimeOffset? from, DateTimeOffset? through)
        => db.QuickIntakes.AsNoTracking().Where(q =>
            (!from.HasValue || q.CreatedUtc >= from.Value) && (!through.HasValue || q.CreatedUtc < through.Value));

    internal static IQueryable<Guid> VerifiedRequestIds(RequestPortalDbContext db, string systemName)
        => db.IntegrationLinks.AsNoTracking().Where(l => l.SystemName == systemName &&
            l.Status == SynchronizationStatus.Synchronized && l.VerifiedUtc != null && l.SynchronizedUtc != null &&
            l.ExternalReference != null && l.ExternalReference != "" &&
            l.ApplicationNumber != null && l.ApplicationNumber != "").Select(l => l.RequestId);
}
