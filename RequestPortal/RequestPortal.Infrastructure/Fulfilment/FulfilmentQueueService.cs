using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Fulfilment;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Fulfilment;

public sealed class FulfilmentQueueService : IFulfilmentQueueService
{
    private readonly RequestPortalDbContext dbContext;

    public FulfilmentQueueService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<IReadOnlyList<FulfilmentQueueItem>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        List<FulfilmentQueueItem> items = await (
            from request in dbContext.AssistanceRequests.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on request.OrganizationId equals organization.Id
            join fulfilment in dbContext.Fulfilments.AsNoTracking()
                on request.Id equals fulfilment.RequestId into fulfilments
            from fulfilment in fulfilments.DefaultIfEmpty()
            where request.Status == RequestStatus.Approved
                || request.Status == RequestStatus.PendingFulfilment
                || request.Status == RequestStatus.Fulfilled
            orderby request.CreatedUtc descending
            select new FulfilmentQueueItem(
                request.Id,
                request.PublicReference,
                organization.Id,
                organization.Name,
                request.Status.ToString(),
                request.Summary,
                fulfilment == null ? null : fulfilment.Id,
                fulfilment == null ? null : fulfilment.Source.ToString(),
                fulfilment == null ? null : fulfilment.Status.ToString(),
                fulfilment == null ? null : fulfilment.ExternalReference,
                request.CreatedUtc))
            .ToListAsync(cancellationToken);

        return items;
    }
}
