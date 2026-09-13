using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Notifications;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Messaging;

public sealed class OutboxDispatcher
{
    private readonly RequestPortalDbContext dbContext;
    private readonly INotificationSender notificationSender;
    private readonly IFfpManagerIntegrationService ffpManagerIntegrationService;
    private readonly FfpManagerBridgeOptions ffpManagerOptions;
    private readonly NotificationOptions notificationOptions;
    private readonly ILogger<OutboxDispatcher> logger;

    public OutboxDispatcher(
        RequestPortalDbContext dbContext,
        INotificationSender notificationSender,
        IFfpManagerIntegrationService ffpManagerIntegrationService,
        IOptions<FfpManagerBridgeOptions> ffpManagerOptions,
        IOptions<NotificationOptions> notificationOptions,
        ILogger<OutboxDispatcher> logger)
    {
        this.dbContext = dbContext;
        this.notificationSender = notificationSender;
        this.ffpManagerIntegrationService = ffpManagerIntegrationService;
        this.ffpManagerOptions = ffpManagerOptions.Value;
        this.notificationOptions = notificationOptions.Value;
        this.logger = logger;
    }

    public async Task<int> DispatchBatchAsync(
        int batchSize = 20,
        CancellationToken cancellationToken = default)
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
            if (ffpManagerOptions.Enabled)
            {
                await EnsureFfpManagerSubmissionQueuedAsync(message, cancellationToken);
            }

            await SendRequestSubmittedAsync(message, cancellationToken);
            return;
        }

        if (outbox.MessageType.EndsWith(nameof(FfpManagerSubmissionRequestedMessage), StringComparison.Ordinal))
        {
            FfpManagerSubmissionRequestedMessage message = Deserialize<FfpManagerSubmissionRequestedMessage>(outbox.Payload);
            if (ffpManagerOptions.Enabled)
            {
                await ffpManagerIntegrationService.SubmitRequestAsync(message.RequestId, cancellationToken);
            }
            return;
        }

        if (outbox.MessageType.EndsWith(nameof(RequestStatusChangedMessage), StringComparison.Ordinal))
        {
            RequestStatusChangedMessage message = Deserialize<RequestStatusChangedMessage>(outbox.Payload);
            await SendRequestStatusChangedAsync(message, cancellationToken);
            return;
        }

        if (outbox.MessageType.EndsWith(nameof(FfpManagerInformationRequestedMessage), StringComparison.Ordinal))
        {
            FfpManagerInformationRequestedMessage message = Deserialize<FfpManagerInformationRequestedMessage>(outbox.Payload);
            await SendFfpManagerInformationRequestedAsync(message, cancellationToken);
            return;
        }

        logger.LogInformation(
            "No notification projector registered for outbox type {MessageType}.",
            outbox.MessageType);
    }

    private async Task EnsureFfpManagerSubmissionQueuedAsync(
        RequestSubmittedMessage source,
        CancellationToken cancellationToken)
    {
        Guid integrationMessageId = CreateDeterministicGuid(source.RequestId, "FFPManagerSubmission");
        bool alreadyTracked = dbContext.OutboxMessages.Local.Any(message => message.Id == integrationMessageId);
        if (!alreadyTracked)
        {
            alreadyTracked = await dbContext.OutboxMessages
                .AsNoTracking()
                .AnyAsync(message => message.Id == integrationMessageId, cancellationToken);
        }

        if (alreadyTracked)
        {
            return;
        }

        FfpManagerSubmissionRequestedMessage message = new(
            integrationMessageId,
            source.OccurredUtc,
            source.RequestId);
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = message.MessageId,
            MessageType = typeof(FfpManagerSubmissionRequestedMessage).FullName
                ?? nameof(FfpManagerSubmissionRequestedMessage),
            Payload = JsonSerializer.Serialize(message),
            OccurredUtc = message.OccurredUtc,
            AttemptCount = 0
        });
    }

    private async Task SendOrganizationApprovedAsync(
        OrganizationApprovedMessage message,
        CancellationToken cancellationToken)
    {
        Organization organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleAsync(item => item.Id == message.OrganizationId, cancellationToken);

        await DeliverEmailAsync(
            organization.RegisteredByUserId,
            organization.Id,
            null,
            $"Organization approved: {organization.Name}",
            $"FFP has approved {organization.Name}. You can now manage representatives and submit requests on its behalf.",
            cancellationToken);
    }

    private async Task SendMembershipRequestedAsync(
        MembershipRequestedMessage message,
        CancellationToken cancellationToken)
    {
        string organizationName = await dbContext.Organizations
            .AsNoTracking()
            .Where(item => item.Id == message.OrganizationId)
            .Select(item => item.Name)
            .SingleAsync(cancellationToken);

        List<Guid> administratorUserIds = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership =>
                membership.OrganizationId == message.OrganizationId &&
                membership.Status == MembershipStatus.Approved &&
                membership.Role == OrganizationRole.Administrator)
            .Select(membership => membership.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (Guid administratorUserId in administratorUserIds)
        {
            await DeliverEmailAsync(
                administratorUserId,
                message.OrganizationId,
                null,
                $"Representative approval required: {organizationName}",
                $"A representative has requested access to {organizationName}. Sign in to Request Portal to approve or reject the membership request.",
                cancellationToken);
        }
    }

    private async Task SendMembershipApprovedAsync(
        MembershipApprovedMessage message,
        CancellationToken cancellationToken)
    {
        string organizationName = await dbContext.Organizations
            .AsNoTracking()
            .Where(item => item.Id == message.OrganizationId)
            .Select(item => item.Name)
            .SingleAsync(cancellationToken);

        await DeliverEmailAsync(
            message.UserId,
            message.OrganizationId,
            null,
            $"Membership approved: {organizationName}",
            $"Your representative membership for {organizationName} has been approved.",
            cancellationToken);
    }

    private async Task SendRequestSubmittedAsync(
        RequestSubmittedMessage message,
        CancellationToken cancellationToken)
    {
        var request = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Where(item => item.Id == message.RequestId)
            .Select(item => new
            {
                item.PublicReference,
                item.SubmittedByUserId,
                item.CreatedByUserId,
                item.OrganizationId
            })
            .SingleAsync(cancellationToken);

        Guid recipientUserId = request.SubmittedByUserId ?? request.CreatedByUserId;
        await DeliverEmailAsync(
            recipientUserId,
            request.OrganizationId,
            message.RequestId,
            $"Request submitted: {request.PublicReference}",
            $"Your request {request.PublicReference} has been submitted to FFP for review.",
            cancellationToken);
    }

    private async Task SendRequestStatusChangedAsync(
        RequestStatusChangedMessage message,
        CancellationToken cancellationToken)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == message.RequestId, cancellationToken);

        Guid recipientUserId = request.SubmittedByUserId ?? request.CreatedByUserId;
        string? reason = request.StatusHistory
            .OrderByDescending(item => item.ChangedUtc)
            .Select(item => item.Reason)
            .FirstOrDefault();

        string body = $"Request {request.PublicReference} is now {request.Status}.";
        if (!string.IsNullOrWhiteSpace(reason))
        {
            body += $"\n\n{reason}";
        }

        await DeliverEmailAsync(
            recipientUserId,
            request.OrganizationId,
            request.Id,
            $"Request status: {request.PublicReference}",
            body,
            cancellationToken);
    }

    private async Task SendFfpManagerInformationRequestedAsync(
        FfpManagerInformationRequestedMessage message,
        CancellationToken cancellationToken)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .AsNoTracking()
            .SingleAsync(item => item.Id == message.RequestId, cancellationToken);

        Guid recipientUserId = request.SubmittedByUserId ?? request.CreatedByUserId;
        string body = $"FFP needs additional information for request {request.PublicReference}.\n\n{message.Message}";
        if (message.DueUtc.HasValue)
        {
            body += $"\n\nPlease respond by {message.DueUtc.Value:yyyy-MM-dd}.";
        }

        body += "\n\nSign in to Request Portal to provide the requested information and supporting documents.";
        await DeliverEmailAsync(
            recipientUserId,
            request.OrganizationId,
            request.Id,
            $"Information required: {request.PublicReference}",
            body,
            cancellationToken);
    }

    private async Task DeliverEmailAsync(
        Guid userId,
        Guid? organizationId,
        Guid? requestId,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        string? accountEmail = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.Email)
            .SingleOrDefaultAsync(cancellationToken);

        NotificationPreference? preference = await dbContext.NotificationPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.UserId == userId && item.Channel == NotificationChannel.Email,
                cancellationToken);

        string destination = preference?.Destination ?? accountEmail ?? "(no email address)";
        NotificationHistory history = new(
            Guid.NewGuid(),
            userId,
            organizationId,
            requestId,
            NotificationChannel.Email,
            destination,
            subject,
            DateTimeOffset.UtcNow);
        dbContext.NotificationHistory.Add(history);

        if (preference is not null && !preference.Enabled)
        {
            history.MarkSkipped("Email notifications are disabled in the user's notification preferences.");
            return;
        }

        if (string.IsNullOrWhiteSpace(accountEmail) && preference is null)
        {
            history.MarkSkipped("No email destination is available for this portal user.");
            return;
        }

        if (!notificationOptions.Enabled)
        {
            history.MarkSkipped("Notification delivery is disabled in the current environment.");
            return;
        }

        try
        {
            await notificationSender.SendAsync(
                new NotificationEnvelope(destination, subject, body),
                cancellationToken);
            history.MarkSent(DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            history.MarkFailed(exception.Message);
            throw;
        }
    }

    private static Guid CreateDeterministicGuid(Guid requestId, string purpose)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{requestId:D}|{purpose}"));
        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private static TMessage Deserialize<TMessage>(string payload)
    {
        return JsonSerializer.Deserialize<TMessage>(payload)
            ?? throw new InvalidOperationException(
                $"Unable to deserialize {typeof(TMessage).Name} outbox payload.");
    }
}
