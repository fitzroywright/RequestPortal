using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Integration;

internal static class IntegrationLegacyHistory
{
    public static async Task PreserveAsync(RequestPortalDbContext db, IntegrationLink link, CancellationToken token)
    {
        if (link.LastError is null || link.ErrorCode is not null ||
            await db.IntegrationAttempts.AnyAsync(item => item.LinkId == link.Id && item.Operation == "HistoricalFailure", token)) return;
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(link.LastError)));
        var when = link.LastAttemptUtc ?? link.CreatedUtc;
        var snapshot = new IntegrationAttempt(link.Id,0,when,"HistoricalFailure");
        snapshot.Fail(when,"HistoricalFailure","Historical","HISTORICAL_" + fingerprint,
            "Historical failure fingerprint preserved before recovery. Historical attempt numbers and unsafe exception payloads are not reconstructed.");
        db.IntegrationAttempts.Add(snapshot);
    }
}
