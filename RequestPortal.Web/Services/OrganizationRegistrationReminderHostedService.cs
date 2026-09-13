using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RequestPortal.Infrastructure.Organizations;

namespace RequestPortal.Web.Services;

public sealed class OrganizationRegistrationReminderHostedService : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<OrganizationRegistrationReminderHostedService> logger;

    public OrganizationRegistrationReminderHostedService(
        IServiceScopeFactory scopeFactory,
        ILogger<OrganizationRegistrationReminderHostedService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                OrganizationRegistrationReminderService service =
                    scope.ServiceProvider.GetRequiredService<OrganizationRegistrationReminderService>();
                int queued = await service.QueueDueRemindersAsync(stoppingToken);
                if (queued > 0)
                {
                    logger.LogInformation("Queued {ReminderCount} organization registration reminder(s).", queued);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Organization registration reminder cycle failed.");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
