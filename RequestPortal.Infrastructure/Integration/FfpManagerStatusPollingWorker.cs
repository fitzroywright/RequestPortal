using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Integration;

public sealed class FfpManagerStatusPollingWorker : BackgroundService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IOptions<FfpManagerBridgeOptions> options;
    private readonly ILogger<FfpManagerStatusPollingWorker> logger;

    public FfpManagerStatusPollingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<FfpManagerBridgeOptions> options,
        ILogger<FfpManagerStatusPollingWorker> logger)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            FfpManagerBridgeOptions current = options.Value;
            if (current.Enabled)
            {
                try
                {
                    await PollAsync(current, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "FFP Manager status polling cycle failed.");
                }
            }

            int seconds = Math.Clamp(current.PollIntervalSeconds, 15, 3600);
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private async Task PollAsync(FfpManagerBridgeOptions options, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        RequestPortalDbContext dbContext = scope.ServiceProvider.GetRequiredService<RequestPortalDbContext>();
        IFfpManagerIntegrationService integrationService = scope.ServiceProvider.GetRequiredService<IFfpManagerIntegrationService>();

        var links = await dbContext.IntegrationLinks
            .AsNoTracking()
            .Where(link =>
                link.SystemName == options.SystemName &&
                link.ExternalReference != null)
            .Select(link => new { link.RequestId, link.ExternalReference })
            .ToListAsync(cancellationToken);

        foreach (var link in links)
        {
            try
            {
                FfpManagerApplicationStatus? status = await integrationService.RefreshStatusAsync(
                    link.RequestId,
                    cancellationToken);
                if (status is null)
                {
                    continue;
                }

                Guid messageId = CreateDeterministicMessageId(
                    link.ExternalReference!,
                    status.StatusCode,
                    status.ChangedUtc);
                await integrationService.ReceiveStatusAsync(
                    messageId,
                    link.RequestId,
                    status,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Unable to refresh FFP Manager status for RequestPortal request {RequestId}.",
                    link.RequestId);
            }
        }
    }

    private static Guid CreateDeterministicMessageId(
        string applicationId,
        string statusCode,
        DateTimeOffset changedUtc)
    {
        string value = $"{applicationId}|{statusCode}|{changedUtc:O}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }
}
