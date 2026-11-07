using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Management;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Management;

public sealed record LegacyRequestLocation(string StorageKey, string ExpectedSha256, byte[] Payload);

/// <summary>Operator-only import of verified location JSON. Never registered in HTTP or worker services.</summary>
public static class RequestLocationRecovery
{
    public static async Task<int> ImportAsync(RequestPortalDbContext db, IReadOnlyList<LegacyRequestLocation> source, CancellationToken ct, string? sourceEvidenceSha256 = null)
    {
        if (sourceEvidenceSha256 is not null && (sourceEvidenceSha256.Length != 64 || !sourceEvidenceSha256.All(Uri.IsHexDigit)))
            throw new InvalidOperationException("RP-LOCATION-IMPORT-EVIDENCE: Invalid geography evidence fingerprint.");
        var validated = source.Select(item =>
        {
            if (item.Payload.Length > 32 * 1024 || item.ExpectedSha256.Length != 64 || !item.ExpectedSha256.All(Uri.IsHexDigit) ||
                !Convert.ToHexStringLower(SHA256.HashData(item.Payload)).Equals(item.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RP-LOCATION-IMPORT-HASH: Legacy location integrity validation failed.");
            var snapshot = JsonSerializer.Deserialize<RequestLocationSnapshot>(item.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("RP-LOCATION-IMPORT-JSON: Legacy location JSON is missing.");
            if (item.StorageKey != RequestLocationStorage.Key(snapshot.RequestId))
                throw new InvalidOperationException("RP-LOCATION-IMPORT-IDENTITY: Legacy key and request identity differ.");
            var row = new PersistedRequestLocation();
            row.Apply(snapshot);
            row.ImportedStorageKey = item.StorageKey; row.ImportedSha256 = item.ExpectedSha256.ToLowerInvariant(); row.ImportedUtc = DateTimeOffset.UtcNow;
            return row;
        }).ToArray();
        if (validated.Select(x => x.RequestId).Distinct().Count() != validated.Length)
            throw new InvalidOperationException("RP-LOCATION-IMPORT-DUPLICATE: Duplicate legacy location identities.");
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(validated.OrderBy(x => x.RequestId)
            .Select(x => new { x.RequestId, x.ImportedStorageKey, x.ImportedSha256 }).ToArray())));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='5s'; SET LOCAL statement_timeout='60s'", ct);
        bool locked = await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(7294321652::bigint) AS \"Value\"").SingleAsync(ct);
        if (!locked) throw new InvalidOperationException("RP-LOCATION-IMPORT-BUSY: Another geography import is running.");
        bool workerIdle = await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(7294321651::bigint) AS \"Value\"").SingleAsync(ct);
        if (!workerIdle) throw new InvalidOperationException("RP-LOCATION-IMPORT-BUSY: A dashboard calculation is still running.");
        var ids = validated.Select(x => x.RequestId).ToArray();
        var known = await db.AssistanceRequests.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToHashSetAsync(ct);
        if (known.Count != validated.Length)
            throw new InvalidOperationException("RP-LOCATION-IMPORT-ORPHAN: A legacy geography record has no matching request; source records are retained.");
        var existing = await db.RequestLocations.Where(x => ids.Contains(x.RequestId)).ToDictionaryAsync(x => x.RequestId, ct);
        foreach (var row in validated)
        {
            if (!existing.TryGetValue(row.RequestId, out var saved)) db.RequestLocations.Add(row);
            else if (saved.ResolvedUtc == row.ResolvedUtc && saved.Snapshot() != row.Snapshot())
                throw new InvalidOperationException("RP-LOCATION-IMPORT-CONFLICT: Conflicting historical locations have the same resolution time.");
            else if (saved.ResolvedUtc < row.ResolvedUtc)
            {
                saved.Apply(row.Snapshot()); saved.ImportedStorageKey = row.ImportedStorageKey;
                saved.ImportedSha256 = row.ImportedSha256; saved.ImportedUtc = row.ImportedUtc;
            }
        }
        var marker = await db.RequestLocationImportStates.SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (marker is null) { marker = new(); db.RequestLocationImportStates.Add(marker); }
        marker.SourceFingerprint = sourceEvidenceSha256 ?? fingerprint; marker.VerifiedUtc = DateTimeOffset.UtcNow; marker.ImportedLocations = validated.Length;
        var aggregate = await db.ManagementDashboardAggregates.SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (aggregate is not null)
        {
            // The verified data source changed. Release the old SharePoint failure cooldown once,
            // retaining the failed status, error, version and snapshot until a real rebuild succeeds.
            aggregate.AttemptCount = 0;
            aggregate.NextRefreshUtc = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return validated.Length;
    }
}
