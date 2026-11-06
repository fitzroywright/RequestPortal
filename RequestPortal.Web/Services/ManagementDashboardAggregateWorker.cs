using RequestPortal.Infrastructure.Management;

namespace RequestPortal.Web.Services;

public sealed class ManagementDashboardAggregateWorker(IServiceScopeFactory scopes,ILogger<ManagementDashboardAggregateWorker> logger) : BackgroundService
{
    public static async Task InitializeAsync(IServiceProvider services,ILogger logger,CancellationToken cancellationToken)
    {
        // Initialization is a startup gate, never a fallback calculation in the HTTP request.
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        while(true)
        {
            await using var scope=services.CreateAsyncScope();
            try {await scope.ServiceProvider.GetRequiredService<ManagementDashboardAggregateRefresher>().RefreshAsync(timeout.Token);}
            catch(Exception error) when(error is not OperationCanceledException)
            {logger.LogError(error,"Management Dashboard startup refresh failed; checking the last valid snapshot.");}
            var db=scope.ServiceProvider.GetRequiredService<RequestPortal.Infrastructure.Persistence.RequestPortalDbContext>();
            db.ChangeTracker.Clear();
            var row=await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.ManagementDashboardAggregates,x=>x.Id==1,timeout.Token);
            if(row.SnapshotVersion>0) {PersistentManagementDashboardService.Read(row);break;}
            await Task.Delay(TimeSpan.FromSeconds(2),timeout.Token);
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope=scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ManagementDashboardAggregateRefresher>().RefreshAsync(stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {break;}
            catch(Exception error) {logger.LogError(error,"Management Dashboard refresh failed; durable requests will retry.");}
            try {await Task.Delay(TimeSpan.FromSeconds(2),stoppingToken);}catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {break;}
        }
    }
}
