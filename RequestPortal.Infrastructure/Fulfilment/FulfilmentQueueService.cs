using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Fulfilment;
using RequestPortal.Domain.Fulfilment;
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

    public async Task<Guid> StartAsync(
        Guid staffUserId,
        Guid requestId,
        string source,
        string? externalReference,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse(source, true, out FulfilmentSource fulfilmentSource))
        {
            throw new ArgumentException("Unknown fulfilment source.", nameof(source));
        }

        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        if (request.Status != RequestStatus.Approved && request.Status != RequestStatus.PendingFulfilment)
        {
            throw new InvalidOperationException("Only approved requests can enter fulfilment.");
        }

        Fulfilment? fulfilment = await dbContext.Fulfilments
            .SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);

        if (fulfilment is null)
        {
            fulfilment = new Fulfilment(
                Guid.NewGuid(),
                request.Id,
                request.OrganizationId,
                fulfilmentSource,
                DateTimeOffset.UtcNow);
            dbContext.Fulfilments.Add(fulfilment);
        }
        else if (fulfilment.Source != fulfilmentSource)
        {
            throw new InvalidOperationException("The request already has a fulfilment with a different source.");
        }

        fulfilment.MarkInProgress(externalReference);
        if (request.Status == RequestStatus.Approved)
        {
            request.MarkPendingFulfilment(staffUserId, DateTimeOffset.UtcNow);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return fulfilment.Id;
    }

    public async Task CompleteAsync(
        Guid staffUserId,
        Guid requestId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        Fulfilment fulfilment = await dbContext.Fulfilments
            .SingleAsync(item => item.RequestId == requestId, cancellationToken);

        if (request.Status == RequestStatus.Fulfilled && fulfilment.Status == FulfilmentStatus.Fulfilled)
        {
            return;
        }

        if (request.Status != RequestStatus.PendingFulfilment)
        {
            throw new InvalidOperationException("The request is not pending fulfilment.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        fulfilment.Complete(now, notes);
        request.MarkFulfilled(staffUserId, now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task CancelAsync(
        Guid staffUserId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        Fulfilment fulfilment = await dbContext.Fulfilments
            .SingleAsync(item => item.RequestId == requestId, cancellationToken);

        fulfilment.Cancel(reason);
        if (request.Status == RequestStatus.PendingFulfilment)
        {
            request.ReturnToApproved(staffUserId, DateTimeOffset.UtcNow, reason);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
