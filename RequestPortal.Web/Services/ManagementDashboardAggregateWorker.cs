using RequestPortal.Infrastructure.Management;

namespace RequestPortal.Web.Services;

public sealed record ManagementDashboardWorkerStatus(
    string Status = "WaitingForApplication", DateTimeOffset? LastAttemptUtc = null,
    DateTimeOffset? LastPublishedUtc = null, DateTimeOffset? LastFailureUtc = null,
    string? FailureType = null);

public sealed class ManagementDashboardWorkerState
{
    private ManagementDashboardWorkerStatus current = new();
    public ManagementDashboardWorkerStatus Current => Volatile.Read(ref current);
    internal void Set(string status, DateTimeOffset now, bool published = false, string? failureType = null)
    {
        var previous = Current;
        Volatile.Write(ref current, previous with
        {
            Status = status,
            LastAttemptUtc = status == "Refreshing" ? now : previous.LastAttemptUtc,
            LastPublishedUtc = published ? now : previous.LastPublishedUtc,
            LastFailureUtc = failureType is not null ? now : previous.LastFailureUtc,
            FailureType = published ? null : failureType ?? previous.FailureType
        });
    }
}

public sealed class ManagementDashboardAggregateWorker(
    IServiceScopeFactory scopes, ILogger<ManagementDashboardAggregateWorker> logger,
    IHostApplicationLifetime lifetime, ManagementDashboardWorkerState state,
    TimeProvider? clock = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var time = clock ?? TimeProvider.System;
        int failures = 0;
        try
        {
            // Kestrel and the ordinary health endpoint must be available before any rebuild.
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
            await started.Task.WaitAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                int delaySeconds = 2;
                try
                {
                    state.Set("Refreshing", time.GetUtcNow());
                    await using var scope = scopes.CreateAsyncScope();
                    bool published = await scope.ServiceProvider.GetRequiredService<IManagementDashboardAggregateRefresher>()
                        .RefreshAsync(stoppingToken);
                    if (published)
                    {
                        failures = 0;
                        logger.LogInformation("Management Dashboard snapshot published successfully.");
                    }
                    state.Set("Waiting", time.GetUtcNow(), published);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    failures = Math.Min(failures + 1, 8);
                    delaySeconds = Math.Min(300, 1 << failures);
                    state.Set("Retrying", time.GetUtcNow(), failureType: error.GetType().Name);
                    // Persisted snapshot and outbox remain authoritative. Never log provider secrets here.
                    logger.LogWarning("Management Dashboard refresh failed ({FailureType}); retrying in {DelaySeconds} seconds.",
                        error.GetType().Name, delaySeconds);
                }
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { state.Set("Stopped", time.GetUtcNow()); }
    }
}
