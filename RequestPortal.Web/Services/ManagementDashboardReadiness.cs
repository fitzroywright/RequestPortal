using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Management;
using RequestPortal.Infrastructure.Management;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Services;

/// <summary>Operational metadata only. Does not calculate, refresh or reveal aggregate contents.</summary>
public static class ManagementDashboardReadiness
{
    public static void MapManagementDashboardReadiness(this WebApplication app)
    {
        app.MapGet("/health/dashboard", async (RequestPortalDbContext db, ManagementDashboardWorkerState worker, HttpContext http) =>
        {
            var result = await ReadAsync(db, worker, http.RequestAborted);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Json(result, statusCode: result.Ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();
    }

    public sealed record Readiness(bool Ready, bool AggregateInitialized, long SnapshotVersion,
        double? SnapshotAgeSeconds, DateTimeOffset? LastSuccessfulRefreshUtc, bool RefreshFailed,
        string RefreshStatus, ManagementDashboardWorkerStatus Worker);

    public static async Task<Readiness> ReadAsync(RequestPortalDbContext db, ManagementDashboardWorkerState worker, CancellationToken ct)
    {
        // Database errors propagate: they are never converted to an initialization/success response.
        var row = await db.ManagementDashboardAggregates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
        bool initialized;
        try { PersistentManagementDashboardService.Read(row); initialized = true; }
        catch (ManagementDashboardInitializingException) { initialized = false; }
        double? age = row?.LastUpdatedUtc is { } updated ? Math.Max(0, (DateTimeOffset.UtcNow - updated).TotalSeconds) : null;
        var workerStatus = worker.Current;
        bool failed = row?.RefreshStatus == "Failed" || workerStatus.FailureType is not null;
        bool ready = initialized && age is <= 120 && !failed && workerStatus.Status is "Waiting" or "Refreshing";
        return new(ready, initialized, row?.SnapshotVersion ?? 0, age, row?.LastUpdatedUtc,
            failed, row?.RefreshStatus ?? "Pending", workerStatus);
    }
}
