using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Messaging;

public sealed class OutboxDispatcher
{
    private readonly RequestPortalDbContext dbContext;
    private readonly INotificationSender notificationSender;
    private readonly ILogger<OutboxDispatcher> logger;

    public OutboxDispatcher(
        RequestPortalDbContext dbContext,
        INotificationSender notificationSender,
        ILogger<OutboxDispatcher> logger)
    {
        this.dbContext = dbContext;
        this.notificationSender = notificationSender;
        this.logger = logger;
    }

    public async Task<int> DispatchBatchAsync(int batchSize = 20, CancellationToken cancellationToken = default)
    {
        List<OutboxMessage> messages = await dbContext.OutboxMessages
            .Where(message => message.ProcessedUtc == null && message.AttemptCount < 10)
            .OrderBy(message => message.OccurredUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        int processed = 0;
        foreach (OutboxMessage message in messages)
        {
            try
            {
                await DispatchAsync(message, cancellationToken);
                message.ProcessedUtc = DateTimeOffset.UtcNow;
                message.LastError = null;
                processed++;
            }
            catch (Exception exception)
            {
                message.AttemptCount++;
                message.LastError = exception.Message;
                logger.LogError(exception, "Outbox message {MessageId} failed.", message.Id);
            }
        }

        if (messages.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return processed;
    }

    private async Task DispatchAsync(OutboxMessage outbox, CancellationToken cancellationToken)
    {
        if (outbox.MessageType.EndsWith(nameof(OrganizationApprovedMessage), StringComparison.Ordinal))
        {
            OrganizationApprovedMessage message = Deserialize<OrganizationApprovedMessage>(outbox.Payload);
            await SendOrganizationApprovedAsync(message, cancellationToken);
            return;
        }

        if (outbox.MessageType.EndsWith(nameof(MembershipRequestedMessage), StringComparison.Ordinal))
        {
            MembershipRequestedMessage message = Deserialize<MembershipRequestedMessage>(outbox.Payload);
            await SendMembershipRequestedAsync(message, cancellationToken);
            return;
        }

        if (outbox.MessageType.EndsWith(nameof(MembershipApprovedMessage), StringComparison.Ordinal))
        {
            MembershipApprovedMessage message = Deserialize<MembershipApprovedMessage>(outbox.Payload);
            await SendMembershipApprovedAsync(message, cancellationToken);
            return;
        }

        if (outbox.MessageType.EndsWith(nameof(RequestSubmittedMessage), StringComparison.Ordinal))
        {
            RequestSubmittedMessage message = Deserialize<RequestSubmittedMessage>(outbox.Payload);
            await SendRequestSubmittedAsync(message, cancellationToken);
            return;
        }

        if (outbox.MessageType.EndsWith(nameof(RequestStatusChangedMessage), StringComparison.Ordinal))
        {
            RequestStatusChangedMessage message = Deserialize<RequestStatusChangedMessage>(outbox.Payload);
            await SendRequestStatusChangedAsync(message, cancellationToken);
            return;
        }

        logger.LogInformation("No notification projector registered for outbox type {MessageType}.", outbox.MessageType);
    }

    private async Task SendOrganizationApprovedAsync(OrganizationApprovedMessage message, CancellationToken cancellationToken)
    {
        Organization organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleAsync(item => item.Id == message.OrganizationId, cancellationToken);

        string? recipient = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == organization.RegisteredByUserId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(recipient))
        {
            await notificationSender.SendAsync(
                new NotificationEnvelope(
                    recipient,
                    $"Organization approved: {organization.Name}",
                    $"FFP has approved {organization.Name}. You can now manage representatives and submit requests on its behalf."),
                cancellationToken);
        }
    }

    private async Task SendMembershipRequestedAsync(MembershipRequestedMessage message, CancellationToken cancellationToken)
    {
        string organizationName = await dbContext.Organizations
            .AsNoTracking()
            .Where(item => item.Id == message.OrganizationId)
            .Select(item => item.Name)
            .SingleAsync(cancellationToken);

        List<string> recipients = await (
            from membership in dbContext.OrganizationMemberships.AsNoTracking()
            join user in dbContext.Users.AsNoTracking() on membership.UserId equals user.Id
            where membership.OrganizationId == message.OrganizationId
                && membership.Status == MembershipStatus.Approved
                && membership.Role == OrganizationRole.Administrator
                && user.Email != null
            select user.Email!)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (string recipient in recipients)
        {
            await notificationSender.SendAsync(
                new NotificationEnvelope(
                    recipient,
                    $"Representative approval required: {organizationName}",
                    $"A representative has requested access to {organizationName}. Sign in to Request Portal to approve or reject the membership request."),
                cancellationToken);
        }
    }

    private async Task SendMembershipApprovedAsync(MembershipApprovedMessage message, CancellationToken cancellationToken)
    {
        string organizationName = await dbContext.Organizations
            .AsNoTracking()
            .Where(item => item.Id == message.OrganizationId)
            .Select(item => item.Name)
            .SingleAsync(cancellationToken);

        string? recipient = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == message.UserId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(recipient))
        {
            await notificationSender.SendAsync(
                new NotificationEnvelope(
                    recipient,
                    $"Membership approved: {organizationName}",
                    $"Your representative membership for {organizationName} has been approved."),
                cancellationToken);
        }
    }

    private async Task SendRequestSubmittedAsync(RequestSubmittedMessage message, CancellationToken cancellationToken)
    {
        var request = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Where(item => item.Id == message.RequestId)
            .Select(item => new { item.PublicReference, item.SubmittedByUserId })
            .SingleAsync(cancellationToken);

        if (!request.SubmittedByUserId.HasValue)
        {
            return;
        }

        string? recipient = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == request.SubmittedByUserId.Value)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(recipient))
        {
            await notificationSender.SendAsync(
                new NotificationEnvelope(
                    recipient,
                    $"Request submitted: {request.PublicReference}",
                    $"Your request {request.PublicReference} has been submitted to FFP for review."),
                cancellationToken);
        }
    }

    private async Task SendRequestStatusChangedAsync(RequestStatusChangedMessage message, CancellationToken cancellationToken)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == message.RequestId, cancellationToken);

        Guid recipientUserId = request.SubmittedByUserId ?? request.CreatedByUserId;
        string? recipient = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == recipientUserId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(recipient))
        {
            return;
        }

        string? reason = request.StatusHistory
            .OrderByDescending(item => item.ChangedUtc)
            .Select(item => item.Reason)
            .FirstOrDefault();

        string body = $"Request {request.PublicReference} is now {request.Status}.";
        if (!string.IsNullOrWhiteSpace(reason))
        {
            body += $"\n\n{reason}";
        }

        await notificationSender.SendAsync(
            new NotificationEnvelope(recipient, $"Request status: {request.PublicReference}", body),
            cancellationToken);
    }

    private static TMessage Deserialize<TMessage>(string payload)
    {
        return JsonSerializer.Deserialize<TMessage>(payload)
            ?? throw new InvalidOperationException($"Unable to deserialize {typeof(TMessage).Name} outbox payload.");
    }
}
