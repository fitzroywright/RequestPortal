using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Fulfilment;
using RequestPortal.Domain.Fulfilment;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Fulfilment;

public sealed class FulfilmentQueueService : IFulfilmentQueueService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IBusinessAuditWriter auditWriter;

    public FulfilmentQueueService(RequestPortalDbContext dbContext, IBusinessAuditWriter auditWriter)
    {
        this.dbContext = dbContext;
        this.auditWriter = auditWriter;
    }

    public async Task<IReadOnlyList<FulfilmentQueueItem>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        return await (
            from request in dbContext.AssistanceRequests.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking() on request.OrganizationId equals organization.Id
            join fulfilment in dbContext.Fulfilments.AsNoTracking() on request.Id equals fulfilment.RequestId into fulfilments
            from fulfilment in fulfilments.DefaultIfEmpty()
            where request.Status == RequestStatus.Approved || request.Status == RequestStatus.PendingFulfilment || request.Status == RequestStatus.Fulfilled
            orderby request.CreatedUtc descending
            select new FulfilmentQueueItem(request.Id, request.PublicReference, organization.Id, organization.Name, request.Status.ToString(), request.Summary, fulfilment == null ? null : fulfilment.Id, fulfilment == null ? null : fulfilment.Source.ToString(), fulfilment == null ? null : fulfilment.Status.ToString(), fulfilment == null ? null : fulfilment.ExternalReference, request.CreatedUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FulfilmentLineView>> GetLinesAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        return await (
            from fulfilment in dbContext.Fulfilments.AsNoTracking()
            join line in dbContext.FulfilmentLines.AsNoTracking() on fulfilment.Id equals line.FulfilmentId
            join requestItem in dbContext.AssistanceRequestItems.AsNoTracking() on line.RequestItemId equals requestItem.Id
            where fulfilment.RequestId == requestId
            orderby requestItem.Description
            select new FulfilmentLineView(line.Id, requestItem.Id, requestItem.Description, line.RequestedQuantity, line.FulfilledQuantity, line.RequestedQuantity - line.FulfilledQuantity, line.FulfilledValue, line.Notes))
            .ToListAsync(cancellationToken);
    }

    public async Task<Guid> StartAsync(Guid staffUserId, Guid requestId, string source, string? externalReference, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse(source, true, out FulfilmentSource fulfilmentSource)) throw new ArgumentException("Unknown fulfilment source.", nameof(source));

        AssistanceRequest request = await dbContext.AssistanceRequests.Include(item => item.StatusHistory).Include(item => item.Items).SingleAsync(item => item.Id == requestId, cancellationToken);
        if (request.Status != RequestStatus.Approved && request.Status != RequestStatus.PendingFulfilment) throw new InvalidOperationException("Only approved requests can enter fulfilment.");

        Fulfilment? fulfilment = await dbContext.Fulfilments.SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
        if (fulfilment is null)
        {
            fulfilment = new Fulfilment(Guid.NewGuid(), request.Id, request.OrganizationId, fulfilmentSource, DateTimeOffset.UtcNow);
            dbContext.Fulfilments.Add(fulfilment);
            foreach (AssistanceRequestItem item in request.Items)
            {
                dbContext.FulfilmentLines.Add(new FulfilmentLine(Guid.NewGuid(), fulfilment.Id, item.Id, item.Quantity));
            }
        }
        else if (fulfilment.Source != fulfilmentSource)
        {
            throw new InvalidOperationException("The request already has a fulfilment with a different source.");
        }

        fulfilment.MarkInProgress(externalReference);
        if (request.Status == RequestStatus.Approved) request.MarkPendingFulfilment(staffUserId, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(staffUserId, "FulfilmentStarted", "Fulfilment", fulfilment.Id, request.OrganizationId, fulfilmentSource.ToString(), cancellationToken);
        return fulfilment.Id;
    }

    public async Task RecordProgressAsync(Guid staffUserId, Guid requestId, Guid requestItemId, decimal quantity, decimal value, string? notes, CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests.AsNoTracking().SingleAsync(item => item.Id == requestId, cancellationToken);
        if (request.Status != RequestStatus.PendingFulfilment) throw new InvalidOperationException("Progress can only be recorded while a request is pending fulfilment.");

        Fulfilment fulfilment = await dbContext.Fulfilments.SingleAsync(item => item.RequestId == requestId, cancellationToken);
        if (fulfilment.Status == FulfilmentStatus.Cancelled || fulfilment.Status == FulfilmentStatus.Fulfilled) throw new InvalidOperationException("This fulfilment no longer accepts progress updates.");

        FulfilmentLine line = await dbContext.FulfilmentLines.SingleAsync(item => item.FulfilmentId == fulfilment.Id && item.RequestItemId == requestItemId, cancellationToken);
        line.RecordProgress(quantity, value, DateTimeOffset.UtcNow, notes);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(staffUserId, "FulfilmentProgressRecorded", "FulfilmentLine", line.Id, request.OrganizationId, $"quantity={quantity}; value={value}", cancellationToken);
    }

    public async Task CompleteAsync(Guid staffUserId, Guid requestId, string? notes, CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests.Include(item => item.StatusHistory).SingleAsync(item => item.Id == requestId, cancellationToken);
        Fulfilment fulfilment = await dbContext.Fulfilments.SingleAsync(item => item.RequestId == requestId, cancellationToken);
        if (request.Status == RequestStatus.Fulfilled && fulfilment.Status == FulfilmentStatus.Fulfilled) return;
        if (request.Status != RequestStatus.PendingFulfilment) throw new InvalidOperationException("The request is not pending fulfilment.");

        List<FulfilmentLine> lines = await dbContext.FulfilmentLines.Where(item => item.FulfilmentId == fulfilment.Id).ToListAsync(cancellationToken);
        if (lines.Count > 0 && lines.Any(line => !line.IsComplete))
        {
            throw new InvalidOperationException("All requested line quantities must be fulfilled before the request can be marked fulfilled.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        fulfilment.Complete(now, notes);
        request.MarkFulfilled(staffUserId, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(staffUserId, "FulfilmentCompleted", "Fulfilment", fulfilment.Id, request.OrganizationId, notes, cancellationToken);
    }

    public async Task CancelAsync(Guid staffUserId, Guid requestId, string reason, CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests.Include(item => item.StatusHistory).SingleAsync(item => item.Id == requestId, cancellationToken);
        Fulfilment fulfilment = await dbContext.Fulfilments.SingleAsync(item => item.RequestId == requestId, cancellationToken);
        fulfilment.Cancel(reason);
        if (request.Status == RequestStatus.PendingFulfilment) request.ReturnToApproved(staffUserId, DateTimeOffset.UtcNow, reason);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(staffUserId, "FulfilmentCancelled", "Fulfilment", fulfilment.Id, request.OrganizationId, reason, cancellationToken);
    }
}
