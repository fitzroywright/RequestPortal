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
        var row=await db.ManagementDashboardAggregates.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==1,cancellationToken)
            ?? throw new ManagementDashboardInitializingException();
        var snapshot=Read(row);
        return snapshot with {SnapshotVersion=row.SnapshotVersion,LastUpdatedUtc=row.LastUpdatedUtc,
            LastReconciledUtc=row.LastReconciledUtc,RefreshStatus=row.RefreshStatus};
    }
    public static ManagementDashboardSnapshot Read(ManagementDashboardAggregate? row)
    {
        if(row is null || row.SnapshotVersion<1 || row.AggregateData is null)
            throw new ManagementDashboardInitializingException();
        try
        {
            var snapshot=JsonSerializer.Deserialize<ManagementDashboardSnapshot>(row.AggregateData);
            Validate(snapshot);
            return snapshot!;
        }
        catch(JsonException) {throw new ManagementDashboardInitializingException();}
    }
    public static void Validate(ManagementDashboardSnapshot? snapshot)
    {
        if(snapshot is null || snapshot.GeneratedUtc==default || snapshot.Executive is null ||
           snapshot.Statuses is null || snapshot.Attention is null || snapshot.Categories is null ||
           snapshot.Programs is null || snapshot.Delivery is null || snapshot.DeliveryAnalytics is null ||
           snapshot.DeliveryAnalytics.Organizations is null || snapshot.DeliveryAnalytics.Programs is null ||
           snapshot.DeliveryAnalytics.Parishes is null || snapshot.DeliveryAnalytics.Trend is null ||
           snapshot.Performance is null || snapshot.Trend is null || snapshot.Parishes is null ||
           snapshot.Parishes.Sum(x=>x.Requests)!=snapshot.Statuses.Sum(x=>x.Count) ||
           snapshot.Executive.Outstanding!=snapshot.Parishes.Sum(x=>x.Outstanding) ||
           snapshot.Executive.Completed!=snapshot.Parishes.Sum(x=>x.Completed))
            throw new ManagementDashboardInitializingException();
    }
    public Task<ManagementDashboardSnapshot> GetDashboardAsync(ManagementDashboardFilter filter,CancellationToken cancellationToken=default) =>
        filter == new ManagementDashboardFilter() ? GetDashboardAsync(cancellationToken) : calculator.GetDashboardAsync(filter,cancellationToken);
    public Task<IReadOnlyList<ManagementRequestRow>> GetRequestsAsync(ManagementRequestFilter filter,CancellationToken cancellationToken=default) => calculator.GetRequestsAsync(filter,cancellationToken);
    public Task<ParishDrillDown> GetParishAsync(string parish,CancellationToken cancellationToken=default) => calculator.GetParishAsync(parish,cancellationToken);
    public Task<IReadOnlyList<ManagementDeliveryRow>> GetDeliveriesAsync(ManagementDeliveryFilter filter,CancellationToken cancellationToken=default) => calculator.GetDeliveriesAsync(filter,cancellationToken);
}
