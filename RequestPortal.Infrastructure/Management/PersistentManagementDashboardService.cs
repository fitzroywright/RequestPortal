using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Management;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Management;

/// <summary>The default page has exactly one database projection and no storage/calculation dependency.</summary>
public sealed class PersistentManagementDashboardService(RequestPortalDbContext db, ManagementDashboardService calculator) : IManagementDashboardService
{
    public async Task<ManagementDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        var row=await db.ManagementDashboardAggregates.AsNoTracking().SingleAsync(x=>x.Id==1,cancellationToken);
        var snapshot=Read(row);
        return snapshot with {SnapshotVersion=row.SnapshotVersion,LastUpdatedUtc=row.LastUpdatedUtc,
            LastReconciledUtc=row.LastReconciledUtc,RefreshStatus=row.RefreshStatus};
    }
    public static ManagementDashboardSnapshot Read(ManagementDashboardAggregate row)
    {
        if(row.SnapshotVersion<1 || row.AggregateData is null)
            throw new InvalidOperationException("Management Dashboard initialization is incomplete.");
        return JsonSerializer.Deserialize<ManagementDashboardSnapshot>(row.AggregateData)
            ?? throw new InvalidOperationException("Management Dashboard snapshot is unavailable.");
    }
    public Task<ManagementDashboardSnapshot> GetDashboardAsync(ManagementDashboardFilter filter,CancellationToken cancellationToken=default) =>
        filter == new ManagementDashboardFilter() ? GetDashboardAsync(cancellationToken) : calculator.GetDashboardAsync(filter,cancellationToken);
    public Task<IReadOnlyList<ManagementRequestRow>> GetRequestsAsync(ManagementRequestFilter filter,CancellationToken cancellationToken=default) => calculator.GetRequestsAsync(filter,cancellationToken);
    public Task<ParishDrillDown> GetParishAsync(string parish,CancellationToken cancellationToken=default) => calculator.GetParishAsync(parish,cancellationToken);
    public Task<IReadOnlyList<ManagementDeliveryRow>> GetDeliveriesAsync(ManagementDeliveryFilter filter,CancellationToken cancellationToken=default) => calculator.GetDeliveriesAsync(filter,cancellationToken);
}
