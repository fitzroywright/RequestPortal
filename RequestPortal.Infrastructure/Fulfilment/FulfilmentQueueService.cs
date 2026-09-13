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
        Fulfilment? fulfilment = await dbContext.Fulfilments.AsNoTracking().SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
        if (fulfilment is null) return Array.Empty<FulfilmentLineView>();

        List<FulfilmentProgressRow> rows = await dbContext.Database.SqlQuery<FulfilmentProgressRow>($"""
            SELECT "Id", "FulfilmentId", "RequestItemId", "RequestedQuantity", "FulfilledQuantity", "FulfilledValue", "LastProgressUtc", "Notes"
            FROM fulfilment_lines
            WHERE "FulfilmentId" = {fulfilment.Id}
            """).ToListAsync(cancellationToken);

        Guid[] itemIds = rows.Select(row => row.RequestItemId).Distinct().ToArray();
        Dictionary<Guid, string> descriptions = await dbContext.AssistanceRequestItems.AsNoTracking()
            .Where(item => itemIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => item.Description, cancellationToken);

        return rows.OrderBy(row => descriptions.GetValueOrDefault(row.RequestItemId, string.Empty))
            .Select(row => new FulfilmentLineView(row.Id, row.RequestItemId, descriptions.GetValueOrDefault(row.RequestItemId, row.RequestItemId.ToString("D")), row.RequestedQuantity, row.FulfilledQuantity, Math.Max(0, row.RequestedQuantity - row.FulfilledQuantity), row.FulfilledValue, row.Notes))
            .ToArray();
    }

    public async Task<Guid> StartAsync(Guid staffUserId, Guid requestId, string source, string? externalReference, CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse(source, true, out FulfilmentSource fulfilmentSource)) throw new ArgumentException("Unknown fulfilment source.", nameof(source));

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        AssistanceRequest request = await dbContext.AssistanceRequests.Include(item => item.StatusHistory).Include(item => item.Items).SingleAsync(item => item.Id == requestId, cancellationToken);
        if (request.Status != RequestStatus.Approved && request.Status != RequestStatus.PendingFulfilment) throw new InvalidOperationException("Only approved requests can enter fulfilment.");

        Fulfilment? fulfilment = await dbContext.Fulfilments.SingleOrDefaultAsync(item => item.RequestId == requestId, cancellationToken);
        bool created = fulfilment is null;
        if (fulfilment is null)
        {
            fulfilment = new Fulfilment(Guid.NewGuid(), request.Id, request.OrganizationId, fulfilmentSource, DateTimeOffset.UtcNow);
            dbContext.Fulfilments.Add(fulfilment);
        }
        else if (fulfilment.Source != fulfilmentSource)
        {
            throw new InvalidOperationException("The request already has a fulfilment with a different source.");
        }

        fulfilment.MarkInProgress(externalReference);
        if (request.Status == RequestStatus.Approved) request.MarkPendingFulfilment(staffUserId, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (created)
        {
            foreach (AssistanceRequestItem item in request.Items)
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO fulfilment_lines ("Id", "FulfilmentId", "RequestItemId", "RequestedQuantity", "FulfilledQuantity", "FulfilledValue")
                    VALUES ({Guid.NewGuid()}, {fulfilment.Id}, {item.Id}, {item.Quantity}, 0, 0)
                    ON CONFLICT ("FulfilmentId", "RequestItemId") DO NOTHING
                    """, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        await auditWriter.WriteAsync(staffUserId, "FulfilmentStarted", "Fulfilment", fulfilment.Id, request.OrganizationId, fulfilmentSource.ToString(), cancellationToken);
        return fulfilment.Id;
    }

    public async Task RecordProgressAsync(Guid staffUserId, Guid requestId, Guid requestItemId, decimal quantity, decimal value, string? notes, CancellationToken cancellationToken = default)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        AssistanceRequest request = await dbContext.AssistanceRequests.AsNoTracking().SingleAsync(item => item.Id == requestId, cancellationToken);
        if (request.Status != RequestStatus.PendingFulfilment) throw new InvalidOperationException("Progress can only be recorded while a request is pending fulfilment.");
        Fulfilment fulfilment = await dbContext.Fulfilments.AsNoTracking().SingleAsync(item => item.RequestId == requestId, cancellationToken);
        if (fulfilment.Status is FulfilmentStatus.Cancelled or FulfilmentStatus.Fulfilled) throw new InvalidOperationException("This fulfilment no longer accepts progress updates.");

        string? normalizedNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        int affected = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE fulfilment_lines
            SET "FulfilledQuantity" = "FulfilledQuantity" + {quantity},
                "FulfilledValue" = "FulfilledValue" + {value},
                "LastProgressUtc" = {DateTimeOffset.UtcNow},
                "Notes" = COALESCE({normalizedNotes}, "Notes")
            WHERE "FulfilmentId" = {fulfilment.Id}
              AND "RequestItemId" = {requestItemId}
              AND "FulfilledQuantity" + {quantity} <= "RequestedQuantity"
            """, cancellationToken);
        if (affected != 1) throw new InvalidOperationException("Progress was not recorded. The line may be missing or the quantity would exceed the request.");

        await auditWriter.WriteAsync(staffUserId, "FulfilmentProgressRecorded", "Fulfilment", fulfilment.Id, request.OrganizationId, $"item={requestItemId:D}; quantity={quantity}; value={value}", cancellationToken);
    }

    public async Task CompleteAsync(Guid staffUserId, Guid requestId, string? notes, CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests.Include(item => item.StatusHistory).SingleAsync(item => item.Id == requestId, cancellationToken);
        Fulfilment fulfilment = await dbContext.Fulfilments.SingleAsync(item => item.RequestId == requestId, cancellationToken);
        if (request.Status == RequestStatus.Fulfilled && fulfilment.Status == FulfilmentStatus.Fulfilled) return;
        if (request.Status != RequestStatus.PendingFulfilment) throw new InvalidOperationException("The request is not pending fulfilment.");

        int incomplete = await dbContext.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::integer AS "Value"
            FROM fulfilment_lines
            WHERE "FulfilmentId" = {fulfilment.Id}
              AND "FulfilledQuantity" < "RequestedQuantity"
            """).SingleAsync(cancellationToken);
        if (incomplete > 0) throw new InvalidOperationException("All requested line quantities must be fulfilled before the request can be marked fulfilled.");

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

    private sealed class FulfilmentProgressRow
    {
        public Guid Id { get; set; }
        public Guid FulfilmentId { get; set; }
        public Guid RequestItemId { get; set; }
        public decimal RequestedQuantity { get; set; }
        public decimal FulfilledQuantity { get; set; }
        public decimal FulfilledValue { get; set; }
        public DateTimeOffset? LastProgressUtc { get; set; }
        public string? Notes { get; set; }
    }
}
