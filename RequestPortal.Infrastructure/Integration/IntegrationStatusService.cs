using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Integration;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Integration;

public sealed class IntegrationStatusService : IIntegrationStatusService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IClock clock;
    private readonly IExternalApplicationIntegrationService ffpManagerIntegrationService;
    private readonly FfpManagerBridgeOptions ffpManagerOptions;
    private readonly IBusinessAuditWriter auditWriter;

    public IntegrationStatusService(
        RequestPortalDbContext dbContext,
        IClock clock,
        IExternalApplicationIntegrationService ffpManagerIntegrationService,
        IOptions<FfpManagerBridgeOptions> ffpManagerOptions,
        IBusinessAuditWriter auditWriter)
    {
        this.dbContext = dbContext;
        this.clock = clock;
        this.ffpManagerIntegrationService = ffpManagerIntegrationService;
        this.ffpManagerOptions = ffpManagerOptions.Value;
        this.auditWriter = auditWriter;
    }

    public async Task<Guid> EnsureLinkAsync(Guid requestId, string systemName, CancellationToken cancellationToken = default)
    {
        IntegrationLink? existing = await dbContext.IntegrationLinks.SingleOrDefaultAsync(item => item.RequestId == requestId && item.SystemName == systemName, cancellationToken);
        if (existing is not null) return existing.Id;
        bool requestExists = await dbContext.AssistanceRequests.AsNoTracking().AnyAsync(item => item.Id == requestId, cancellationToken);
        if (!requestExists) throw new InvalidOperationException("The request does not exist.");
        IntegrationLink link = new(Guid.NewGuid(), requestId, systemName, clock.UtcNow);
        dbContext.IntegrationLinks.Add(link);
        await dbContext.SaveChangesAsync(cancellationToken);
        return link.Id;
    }

    public async Task<IReadOnlyList<IntegrationStatusSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await (from link in dbContext.IntegrationLinks.AsNoTracking()
                      join request in dbContext.AssistanceRequests.AsNoTracking() on link.RequestId equals request.Id
                      join organization in dbContext.Organizations.AsNoTracking() on request.OrganizationId equals organization.Id
                      orderby request.CreatedUtc descending
                      select new IntegrationStatusSummary(link.Id, request.Id, request.PublicReference, organization.Name, link.SystemName, link.Status, link.ExternalReference, link.LastAttemptUtc, link.SynchronizedUtc, link.LastError))
            .ToListAsync(cancellationToken);
    }

    public async Task MarkPendingAsync(Guid linkId, CancellationToken cancellationToken = default)
    {
        IntegrationLink link = await GetLinkAsync(linkId, cancellationToken);
        link.MarkPending(clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkSynchronizedAsync(Guid linkId, string externalReference, CancellationToken cancellationToken = default)
    {
        IntegrationLink link = await GetLinkAsync(linkId, cancellationToken);
        link.MarkSynchronized(externalReference, clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(Guid linkId, string error, CancellationToken cancellationToken = default)
    {
        IntegrationLink link = await GetLinkAsync(linkId, cancellationToken);
        link.MarkFailed(clock.UtcNow, error);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RetryAsync(Guid staffUserId, Guid linkId, CancellationToken cancellationToken = default)
    {
        IntegrationLink link = await GetLinkAsync(linkId, cancellationToken);
        EnsureSupported(link);

        try
        {
            await ffpManagerIntegrationService.SubmitRequestAsync(link.RequestId, cancellationToken);
            await auditWriter.WriteAsync(staffUserId, "IntegrationRetried", "IntegrationLink", link.Id, null, link.SystemName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await auditWriter.WriteAsync(
                staffUserId,
                "IntegrationRetryFailed",
                "IntegrationLink",
                link.Id,
                null,
                $"system={link.SystemName}; failureType={exception.GetType().Name}",
                cancellationToken);
            throw;
        }
    }

    public async Task ReconcileAsync(Guid staffUserId, Guid linkId, CancellationToken cancellationToken = default)
    {
        IntegrationLink link = await GetLinkAsync(linkId, cancellationToken);
        EnsureSupported(link);
        if (string.IsNullOrWhiteSpace(link.ExternalReference))
        {
            throw new InvalidOperationException("The integration has no external reference to reconcile. Retry the submission first.");
        }

        try
        {
            ExternalApplicationStatus? status = await ffpManagerIntegrationService.RefreshStatusAsync(link.RequestId, cancellationToken);
            if (status is null)
            {
                throw new InvalidOperationException("The external system did not return a status for this request.");
            }

            Guid messageId = CreateDeterministicMessageId(link.ExternalReference, status.StatusCode, status.ChangedUtc);
            await ffpManagerIntegrationService.ReceiveStatusAsync(messageId, link.RequestId, status, cancellationToken);
            await auditWriter.WriteAsync(staffUserId, "IntegrationReconciled", "IntegrationLink", link.Id, null, $"{link.SystemName}:{status.StatusCode}", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await auditWriter.WriteAsync(
                staffUserId,
                "IntegrationReconciliationFailed",
                "IntegrationLink",
                link.Id,
                null,
                $"system={link.SystemName}; failureType={exception.GetType().Name}",
                cancellationToken);
            throw;
        }
    }

    private void EnsureSupported(IntegrationLink link)
    {
        if (!ffpManagerOptions.Enabled || !string.Equals(link.SystemName, ffpManagerOptions.SystemName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Integration '{link.SystemName}' is disabled or has no commissioned replay/reconciliation adapter.");
        }
    }

    private Task<IntegrationLink> GetLinkAsync(Guid linkId, CancellationToken cancellationToken) => dbContext.IntegrationLinks.SingleAsync(item => item.Id == linkId, cancellationToken);

    private static Guid CreateDeterministicMessageId(string externalReference, string statusCode, DateTimeOffset changedUtc)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{externalReference}|{statusCode}|{changedUtc:O}"));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
}
