using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Organizations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Services;

public sealed class OutboxHostedService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<OutboxHostedService> logger;

    public OutboxHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxHostedService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset nextRegistrationReminderProjectionUtc = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                IServiceProvider services = scope.ServiceProvider;

                if (DateTimeOffset.UtcNow >= nextRegistrationReminderProjectionUtc)
                {
                    RequestPortalDbContext dbContext = services.GetRequiredService<RequestPortalDbContext>();
                    OrganizationRegistrationReminderService reminderService = new(dbContext);
                    int queued = await reminderService.QueueDueRemindersAsync(stoppingToken);
                    if (queued > 0)
                    {
                        logger.LogInformation("Queued {ReminderCount} organization registration reminder(s).", queued);
                    }
                    nextRegistrationReminderProjectionUtc = DateTimeOffset.UtcNow.AddHours(6);
                }

                OutboxDispatcher dispatcher = services.GetRequiredService<OutboxDispatcher>();
                await dispatcher.DispatchBatchAsync(cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Request Portal outbox dispatch cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
