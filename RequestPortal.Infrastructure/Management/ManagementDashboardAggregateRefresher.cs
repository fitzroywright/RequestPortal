using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Management;

/// <summary>Consumes only dashboard messages from the existing durable outbox.</summary>
public sealed class ManagementDashboardAggregateRefresher(RequestPortalDbContext db, ManagementDashboardService calculator, TimeProvider? clock=null)
{
    public const string MessageType="RequestPortal.ManagementDashboardRefreshRequested.v1";
    // Transaction-scoped, cluster-wide mutex. Released on commit, rollback, connection loss or process death.
    private const long LockId=7294321651;
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();
    private Task<bool> LockAsync(CancellationToken ct) => db.Database
        .SqlQueryRaw<bool>($"SELECT pg_try_advisory_xact_lock({LockId}::bigint) AS \"Value\"").SingleAsync(ct);

    public async Task<bool> RefreshAsync(CancellationToken ct=default)
    {
        long? attemptedVersion=null;
        try
        {
            await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
            if(!await LockAsync(ct)) return false;
            var row=await db.ManagementDashboardAggregates.SingleAsync(x=>x.Id==1,ct);
            attemptedVersion=row.SnapshotVersion;
            var now=Now;
            if(row.AttemptCount>0 && row.NextRefreshUtc>now) return false;
            bool pending=await db.OutboxMessages.AnyAsync(m=>m.MessageType==MessageType && m.ProcessedUtc==null,ct);
            if(!pending && row.SnapshotVersion>0 && row.NextRefreshUtc>now) return false;

            // All DB reads and acknowledgements see one MVCC snapshot. Events committed later remain pending.
            var snapshot=await calculator.BuildAggregateAsync(ct);
            string json=JsonSerializer.Serialize(snapshot);
            var check=JsonSerializer.Deserialize<RequestPortal.Application.Management.ManagementDashboardSnapshot>(json)
                ?? throw new InvalidOperationException("Aggregate verification failed.");
            if(check.Parishes.Sum(x=>x.Requests)!=check.Statuses.Sum(x=>x.Count))
                throw new InvalidOperationException("Aggregate totals do not reconcile.");
            row.AggregateData=json;row.SnapshotVersion++;row.LastUpdatedUtc=Now;
            if(row.LastReconciledUtc is null || row.LastReconciledUtc<=now.AddMinutes(-15)) row.LastReconciledUtc=Now;
            row.RefreshStatus="Current";row.LastError=null;row.AttemptCount=0;row.NextRefreshUtc=Now.AddMinutes(1);
            await db.SaveChangesAsync(ct);
            await db.OutboxMessages.Where(m=>m.MessageType==MessageType && m.ProcessedUtc==null)
                .ExecuteUpdateAsync(set=>set.SetProperty(m=>m.ProcessedUtc,(DateTimeOffset?)Now)
                    .SetProperty(m=>m.LastError,(string?)null),ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) {throw;}
        catch
        {
            // The build transaction has rolled back. Never replace the last valid JSON on failure.
            db.ChangeTracker.Clear();
            await using var failure=await db.Database.BeginTransactionAsync(ct);
            if(await LockAsync(ct))
            {
                var row=await db.ManagementDashboardAggregates.SingleAsync(x=>x.Id==1,ct);
                if(attemptedVersion is not null && row.SnapshotVersion!=attemptedVersion)return false;
                row.RefreshStatus="Failed";row.LastError="Refresh failed; the previous valid snapshot is retained.";
                row.AttemptCount=Math.Min(row.AttemptCount+1,10);
                row.NextRefreshUtc=Now.AddSeconds(Math.Min(300,Math.Pow(2,row.AttemptCount)));
                await db.SaveChangesAsync(ct);await failure.CommitAsync(ct);
            }
            throw;
        }
    }
}
