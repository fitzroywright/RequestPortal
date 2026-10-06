using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Messages;
using RequestPortal.Infrastructure.Organizations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Services;

public sealed class OutboxHostedService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IConfiguration configuration;
    private readonly ILogger<OutboxHostedService> logger;

    public OutboxHostedService(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<OutboxHostedService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.configuration = configuration;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset nextRegistrationReminderProjectionUtc = DateTimeOffset.MinValue;
        DateTimeOffset nextMessageArchiveUtc = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                IServiceProvider services = scope.ServiceProvider;

                if (DateTimeOffset.UtcNow >= nextRegistrationReminderProjectionUtc)
                {
                    RequestPortalDbContext dbContext = services.GetRequiredService<RequestPortalDbContext>();
                    int[] warningDays = configuration
                        .GetSection("Organizations:RenewalWarningDays")
                        .Get<int[]>() ?? [60, 30, 7];
                    warningDays = warningDays.Where(day => day > 0).Distinct().ToArray();

                    OrganizationRegistrationReminderService reminderService = new(dbContext, warningDays);
                    int queued = await reminderService.QueueDueRemindersAsync(stoppingToken);
                    if (queued > 0)
                    {
                        logger.LogInformation("Queued {ReminderCount} organization registration reminder(s).", queued);
                    }
                    nextRegistrationReminderProjectionUtc = DateTimeOffset.UtcNow.AddHours(6);
                }

                if (DateTimeOffset.UtcNow >= nextMessageArchiveUtc)
                {
                    int archiveAfterDays = Math.Max(
                        1,
                        configuration.GetValue("Messaging:ArchiveAfterDays", 365));
                    DateTimeOffset cutoffUtc = DateTimeOffset.UtcNow.AddDays(-archiveAfterDays);
                    MessageArchiveService archiveService = services.GetRequiredService<MessageArchiveService>();
                    int archived = await archiveService.ArchiveInactiveThreadsAsync(cutoffUtc, stoppingToken);
                    if (archived > 0)
                    {
                        logger.LogInformation(
                            "Archived {ConversationCount} inactive message conversation(s) older than {ArchiveAfterDays} days.",
                            archived,
                            archiveAfterDays);
                    }
                    nextMessageArchiveUtc = DateTimeOffset.UtcNow.AddHours(6);
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
