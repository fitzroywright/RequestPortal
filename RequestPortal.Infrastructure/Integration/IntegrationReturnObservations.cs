using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Integration;

namespace RequestPortal.Infrastructure.Integration;

public sealed partial class IntegrationStatusService
{
    private static readonly Guid ReturnSystemId = new("00000000-0000-0000-0000-00000000f002");
    private static readonly Guid IntegrationSystemActorId = new("00000000-0000-0000-0000-00000000f001");

    // Persist changes to return-event health separately from submission outcomes, using the existing audit store.
    public async Task ObserveReturnOutboxAsync(CancellationToken cancellationToken = default)
    {
        ExternalReturnOutboxStatus? snapshot = null;
        string? code = null;
        try { snapshot = await bridgeClient.GetReturnOutboxAsync(cancellationToken); }
        catch (OperationCanceledException) when(cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { code = "RETURN_OUTBOX_UNAVAILABLE"; }
        string detail = JsonSerializer.Serialize(new ReturnState(snapshot,code));
        var previous = await dbContext.AuditEntries.AsNoTracking().Where(item => item.EntityType == "IntegrationReturnOutbox" && item.EntityId == ReturnSystemId)
            .OrderByDescending(item => item.OccurredUtc).Select(item => item.Detail).FirstOrDefaultAsync(cancellationToken);
        if (detail == previous) return;
        await auditWriter.WriteAsync(IntegrationSystemActorId, "IntegrationReturnOutboxObserved", "IntegrationReturnOutbox", ReturnSystemId,
            null, detail, cancellationToken);
    }

    public async Task<IReadOnlyList<IntegrationReturnObservation>> GetReturnHistoryAsync(CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.AuditEntries.AsNoTracking().Where(item => item.EntityType == "IntegrationReturnOutbox" && item.EntityId == ReturnSystemId)
            .OrderByDescending(item => item.OccurredUtc).Take(100).Select(item => new { item.OccurredUtc, item.Detail }).ToListAsync(cancellationToken);
        return rows.Select(item => {
            var state = JsonSerializer.Deserialize<ReturnState>(item.Detail ?? "null");
            return new IntegrationReturnObservation(item.OccurredUtc,state?.Snapshot,state?.ErrorCode);
        }).ToArray();
    }
    private sealed record ReturnState(ExternalReturnOutboxStatus? Snapshot,string? ErrorCode);

    private async Task<int> GetReturnFailureCountAsync(CancellationToken cancellationToken)
    {
        var detail = await dbContext.AuditEntries.AsNoTracking().Where(item => item.EntityType == "IntegrationReturnOutbox" && item.EntityId == ReturnSystemId)
            .OrderByDescending(item => item.OccurredUtc).Select(item => item.Detail).FirstOrDefaultAsync(cancellationToken);
        if (detail is null) return 0;
        var state = JsonSerializer.Deserialize<ReturnState>(detail);
        return state?.Snapshot?.Failed ?? (state?.ErrorCode is null ? 0 : 1);
    }
}
