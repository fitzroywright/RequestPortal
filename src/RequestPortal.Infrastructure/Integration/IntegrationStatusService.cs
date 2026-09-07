using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Integration;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Integration;

public sealed class IntegrationStatusService : IIntegrationStatusService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IClock clock;

    public IntegrationStatusService(RequestPortalDbContext dbContext, IClock clock)
    {
        this.dbContext = dbContext;
        this.clock = clock;
    }

    public async Task<Guid> EnsureLinkAsync(
        Guid requestId,
        string systemName,
        CancellationToken cancellationToken = default)
    {
        IntegrationLink? existing = await dbContext.IntegrationLinks
            .SingleOrDefaultAsync(
                item => item.RequestId == requestId && item.SystemName == systemName,
                cancellationToken);

        if (existing is not null)
        {
            return existing.Id;
        }

        bool requestExists = await dbContext.AssistanceRequests
            .AsNoTracking()
            .AnyAsync(item => item.Id == requestId, cancellationToken);

        if (!requestExists)
        {
            throw new InvalidOperationException("The request does not exist.");
        }

        IntegrationLink link = new(Guid.NewGuid(), requestId, systemName, clock.UtcNow);
        dbContext.IntegrationLinks.Add(link);
        await dbContext.SaveChangesAsync(cancellationToken);
        return link.Id;
    }

    public async Task<IReadOnlyList<IntegrationStatusSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        List<IntegrationStatusSummary> statuses = await (
            from link in dbContext.IntegrationLinks.AsNoTracking()
            join request in dbContext.AssistanceRequests.AsNoTracking() on link.RequestId equals request.Id
            join organization in dbContext.Organizations.AsNoTracking() on request.OrganizationId equals organization.Id
            orderby request.CreatedUtc descending
            select new IntegrationStatusSummary(
                link.Id,
                request.Id,
                request.PublicReference,
                organization.Name,
                link.SystemName,
                link.Status,
                link.ExternalReference,
                link.LastAttemptUtc,
                link.SynchronizedUtc,
                link.LastError))
            .ToListAsync(cancellationToken);

        return statuses;
    }

    public async Task MarkPendingAsync(Guid linkId, CancellationToken cancellationToken = default)
    {
        IntegrationLink link = await GetLinkAsync(linkId, cancellationToken);
        link.MarkPending(clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkSynchronizedAsync(
        Guid linkId,
        string externalReference,
        CancellationToken cancellationToken = default)
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

    private Task<IntegrationLink> GetLinkAsync(Guid linkId, CancellationToken cancellationToken)
    {
        return dbContext.IntegrationLinks.SingleAsync(item => item.Id == linkId, cancellationToken);
    }
}
