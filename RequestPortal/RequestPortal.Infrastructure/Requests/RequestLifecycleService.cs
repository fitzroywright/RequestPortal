using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Requests;
using RequestPortal.Application.Security;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Fulfilment;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;
using RequestFulfilment = RequestPortal.Domain.Fulfilment.Fulfilment;

namespace RequestPortal.Infrastructure.Requests;

public sealed class RequestLifecycleService : IRequestLifecycleService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IOrganizationAccessService organizationAccessService;
    private readonly IBusinessAuditWriter auditWriter;
    private readonly IClock clock;

    public RequestLifecycleService(
        RequestPortalDbContext dbContext,
        IOrganizationAccessService organizationAccessService,
        IBusinessAuditWriter auditWriter,
        IClock clock)
    {
        this.dbContext = dbContext;
        this.organizationAccessService = organizationAccessService;
        this.auditWriter = auditWriter;
        this.clock = clock;
    }

    public async Task CancelAsync(
        Guid userId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        bool allowed = await organizationAccessService.IsApprovedMemberAsync(userId, request.OrganizationId, cancellationToken);
        if (!allowed)
        {
            throw new UnauthorizedAccessException("The current user cannot cancel this request.");
        }

        DateTimeOffset now = clock.UtcNow;
        request.Cancel(userId, now, reason);
        AddStatusOutbox(request, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(userId, "RequestCancelled", "AssistanceRequest", request.Id, request.OrganizationId, reason, cancellationToken);
    }

    public async Task MarkDuplicateAsync(
        Guid staffUserId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        DateTimeOffset now = clock.UtcNow;
        request.MarkDuplicate(staffUserId, now, reason);
        AddStatusOutbox(request, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(staffUserId, "RequestMarkedDuplicate", "AssistanceRequest", request.Id, request.OrganizationId, reason, cancellationToken);
    }

    public async Task<Guid> StartFulfilmentAsync(
        Guid staffUserId,
        Guid requestId,
        FulfilmentSource source,
        CancellationToken cancellationToken = default)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        DateTimeOffset now = clock.UtcNow;
        request.MarkPendingFulfilment(staffUserId, now);

        RequestFulfilment fulfilment = new(Guid.NewGuid(), request.Id, request.OrganizationId, source, now);
        dbContext.Fulfilments.Add(fulfilment);
        AddStatusOutbox(request, now);

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await auditWriter.WriteAsync(staffUserId, "FulfilmentStarted", "Fulfilment", fulfilment.Id, request.OrganizationId, source.ToString(), cancellationToken);
        return fulfilment.Id;
    }

    public async Task CompleteFulfilmentAsync(
        Guid staffUserId,
        Guid fulfilmentId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        RequestFulfilment fulfilment = await dbContext.Fulfilments
            .SingleAsync(item => item.Id == fulfilmentId, cancellationToken);

        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == fulfilment.RequestId, cancellationToken);

        DateTimeOffset now = clock.UtcNow;
        fulfilment.Complete(now, notes);
        request.MarkFulfilled(staffUserId, now);
        AddStatusOutbox(request, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(staffUserId, "FulfilmentCompleted", "Fulfilment", fulfilment.Id, request.OrganizationId, notes, cancellationToken);
    }

    private void AddStatusOutbox(AssistanceRequest request, DateTimeOffset now)
    {
        RequestStatusChangedMessage message = new(Guid.NewGuid(), now, request.Id, (int)request.Status);
        OutboxMessage outboxMessage = new()
        {
            Id = message.MessageId,
            MessageType = typeof(RequestStatusChangedMessage).FullName ?? nameof(RequestStatusChangedMessage),
            Payload = JsonSerializer.Serialize(message),
            OccurredUtc = message.OccurredUtc,
            AttemptCount = 0
        };

        dbContext.OutboxMessages.Add(outboxMessage);
    }
}
