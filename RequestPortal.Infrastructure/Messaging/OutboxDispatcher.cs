using System;
using System.Collections.Generic;
using System.Globalization;
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
using RequestPortal.Application.Portal;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Notifications;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Messaging;

public sealed class OutboxDispatcher
{
    private const int MaxAttempts = 10;
    private const string RetryAtPrefix = "RETRY_AT=";
    private readonly RequestPortalDbContext dbContext;
    private readonly INotificationSender notificationSender;
    private readonly IFfpManagerIntegrationService ffpManagerIntegrationService;
    private readonly FfpManagerBridgeOptions ffpManagerOptions;
    private readonly NotificationOptions notificationOptions;
    private readonly ILogger<OutboxDispatcher> logger;
    private readonly IRequestPortalOperationsFlowPublisher operationsFlowPublisher;

    public OutboxDispatcher(
        RequestPortalDbContext dbContext,
        INotificationSender notificationSender,
        IFfpManagerIntegrationService ffpManagerIntegrationService,
        IOptions<FfpManagerBridgeOptions> ffpManagerOptions,
        IOptions<NotificationOptions> notificationOptions,
        ILogger<OutboxDispatcher> logger,
        IRequestPortalOperationsFlowPublisher operationsFlowPublisher)
    {
        this.dbContext = dbContext;
        this.notificationSender = notificationSender;
        this.ffpManagerIntegrationService = ffpManagerIntegrationService;
        this.ffpManagerOptions = ffpManagerOptions.Value;
        this.notificationOptions = notificationOptions.Value;
        this.logger = logger;
        this.operationsFlowPublisher = operationsFlowPublisher;
    }

    public async Task<int> DispatchBatchAsync(int batchSize = 20, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<OutboxMessage> candidates = await dbContext.OutboxMessages
            .Where(message => message.ProcessedUtc == null && message.AttemptCount < MaxAttempts)
            .OrderBy(message => message.OccurredUtc)
            .Take(Math.Max(batchSize * 4, batchSize))
            .ToListAsync(cancellationToken);

        List<OutboxMessage> messages = candidates.Where(message => IsDue(message, now)).Take(batchSize).ToList();
        int processed = 0;
        foreach (OutboxMessage message in messages)
        {
            HashSet<Guid> failedHistoryBeforeDispatch = dbContext.NotificationHistory.Local
                .Where(item => item.Status == NotificationDeliveryStatus.Failed)
                .Select(item => item.Id)
                .ToHashSet();

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
                bool exhausted = message.AttemptCount >= MaxAttempts;

                if (exhausted)
                {
                    message.LastError = $"DEAD LETTER: {exception.Message}";
                    foreach (NotificationHistory history in dbContext.NotificationHistory.Local.Where(item =>
                        item.Status == NotificationDeliveryStatus.Failed &&
                        !failedHistoryBeforeDispatch.Contains(item.Id)))
                    {
                        history.MarkDeadLettered(exception.Message);
                    }
                    logger.LogCritical(exception, "Outbox message {MessageId} exhausted {AttemptCount} attempts and was dead-lettered.", message.Id, message.AttemptCount);
                }
                else
                {
                    DateTimeOffset retryAt = DateTimeOffset.UtcNow.Add(GetRetryDelay(message.AttemptCount));
                    message.LastError = $"{RetryAtPrefix}{retryAt:O}|{exception.Message}";
                    logger.LogError(
                        exception,
                        "Outbox message {MessageId} failed on attempt {AttemptCount}; next retry is due at {RetryAtUtc}.",
                        message.Id,
                        message.AttemptCount,
                        retryAt);
                }
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
            await SendOrganizationApprovedAsync(Deserialize<OrganizationApprovedMessage>(outbox.Payload), cancellationToken);
            return;
        }
        if (outbox.MessageType.EndsWith(nameof(OrganizationRegistrationReminderMessage), StringComparison.Ordinal))
        {
            await SendOrganizationRegistrationReminderAsync(Deserialize<OrganizationRegistrationReminderMessage>(outbox.Payload), cancellationToken);
            return;
        }
        if (outbox.MessageType.EndsWith(nameof(OrganizationRegistrationVerifiedMessage), StringComparison.Ordinal))
        {
            await SendOrganizationRegistrationVerifiedAsync(Deserialize<OrganizationRegistrationVerifiedMessage>(outbox.Payload), cancellationToken);
            return;
        }
        if (outbox.MessageType.EndsWith(nameof(MembershipRequestedMessage), StringComparison.Ordinal))
        {
            await SendMembershipRequestedAsync(Deserialize<MembershipRequestedMessage>(outbox.Payload), cancellationToken);
            return;
        }
        if (outbox.MessageType.EndsWith(nameof(MembershipApprovedMessage), StringComparison.Ordinal))
        {
            await SendMembershipApprovedAsync(Deserialize<MembershipApprovedMessage>(outbox.Payload), cancellationToken);
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
                try
                {
                    await ffpManagerIntegrationService.SubmitRequestAsync(message.RequestId, cancellationToken);
                    operationsFlowPublisher.TryEnqueue(
                        message.RequestId,
                        "Downstream",
                        DateTimeOffset.UtcNow,
                        "FFP Manager downstream submission completed.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    operationsFlowPublisher.TryEnqueue(
                        message.RequestId,
                        "Downstream",
                        DateTimeOffset.UtcNow,
                        $"Downstream submission failed with {exception.GetType().Name}.",
                        failed: true);
                    throw;
                }
            }
            return;
        }
        if (outbox.MessageType.EndsWith(nameof(RequestStatusChangedMessage), StringComparison.Ordinal))
        {
            await SendRequestStatusChangedAsync(Deserialize<RequestStatusChangedMessage>(outbox.Payload), cancellationToken);
            return;
        }
        if (outbox.MessageType.EndsWith(nameof(FfpManagerInformationRequestedMessage), StringComparison.Ordinal))
        {
            await SendFfpManagerInformationRequestedAsync(Deserialize<FfpManagerInformationRequestedMessage>(outbox.Payload), cancellationToken);
            return;
        }

        logger.LogInformation("No notification projector registered for outbox type {MessageType}.", outbox.MessageType);
    }

    private async Task EnsureFfpManagerSubmissionQueuedAsync(RequestSubmittedMessage source, CancellationToken cancellationToken)
    {
        Guid integrationMessageId = CreateDeterministicGuid(source.RequestId, "FFPManagerSubmission");
        bool alreadyTracked = dbContext.OutboxMessages.Local.Any(message => message.Id == integrationMessageId);
        if (!alreadyTracked)
        {
            alreadyTracked = await dbContext.OutboxMessages.AsNoTracking().AnyAsync(message => message.Id == integrationMessageId, cancellationToken);
        }
        if (alreadyTracked) return;

        FfpManagerSubmissionRequestedMessage message = new(integrationMessageId, source.OccurredUtc, source.RequestId);
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = message.MessageId,
            MessageType = typeof(FfpManagerSubmissionRequestedMessage).FullName ?? nameof(FfpManagerSubmissionRequestedMessage),
            Payload = JsonSerializer.Serialize(message),
            OccurredUtc = message.OccurredUtc,
            AttemptCount = 0
        });
    }

    private async Task SendOrganizationApprovedAsync(OrganizationApprovedMessage message, CancellationToken cancellationToken)
    {
        Organization organization = await dbContext.Organizations.AsNoTracking().SingleAsync(item => item.Id == message.OrganizationId, cancellationToken);
        await DeliverMandatoryNotificationsAsync(
            organization.RegisteredByUserId,
            organization.Id,
            null,
            $"Organization approved: {organization.Name}",
            $"FFP has approved {organization.Name}. You can now manage representatives and submit requests on its behalf.",
            cancellationToken);
    }

    private async Task SendOrganizationRegistrationReminderAsync(OrganizationRegistrationReminderMessage message, CancellationToken cancellationToken)
    {
        Organization organization = await dbContext.Organizations.AsNoTracking().SingleAsync(item => item.Id == message.OrganizationId, cancellationToken);
        string subject;
        string body;
        if (string.Equals(message.ReminderKind, "Expired", StringComparison.Ordinal))
        {
            subject = $"Registration expired: {organization.Name}";
            body = $"The registration on file for {organization.Name} expired on {message.ExpiryDate:yyyy-MM-dd}. New request submissions are blocked until current registration information is provided and verified by FFP.";
        }
        else
        {
            subject = $"Registration renewal required: {organization.Name}";
            body = $"The registration on file for {organization.Name} expires on {message.ExpiryDate:yyyy-MM-dd}. Please upload current registration evidence before the expiry date to avoid interruption to new request submissions.";
        }

        await DeliverMandatoryNotificationsAsync(
            organization.RegisteredByUserId,
            organization.Id,
            null,
            subject,
            body,
            cancellationToken);
    }

    private async Task SendOrganizationRegistrationVerifiedAsync(OrganizationRegistrationVerifiedMessage message, CancellationToken cancellationToken)
    {
        Organization organization = await dbContext.Organizations.AsNoTracking().SingleAsync(item => item.Id == message.OrganizationId, cancellationToken);
        await DeliverMandatoryNotificationsAsync(
            organization.RegisteredByUserId,
            organization.Id,
            null,
            $"Registration evidence verified: {organization.Name}",
            $"FFP has verified the current registration evidence submitted for {organization.Name}. The verification is now recorded in Request Portal.",
            cancellationToken);
    }

    private async Task SendMembershipRequestedAsync(MembershipRequestedMessage message, CancellationToken cancellationToken)
    {
        string organizationName = await dbContext.Organizations.AsNoTracking().Where(item => item.Id == message.OrganizationId).Select(item => item.Name).SingleAsync(cancellationToken);
        List<Guid> administratorUserIds = await dbContext.OrganizationMemberships.AsNoTracking()
            .Where(membership => membership.OrganizationId == message.OrganizationId && membership.Status == MembershipStatus.Approved && membership.Role == OrganizationRole.Administrator)
            .Select(membership => membership.UserId).Distinct().ToListAsync(cancellationToken);
        foreach (Guid administratorUserId in administratorUserIds)
        {
            await DeliverMandatoryNotificationsAsync(administratorUserId, message.OrganizationId, null, $"Representative approval required: {organizationName}", $"A representative has requested access to {organizationName}. Sign in to Request Portal to approve or reject the membership request.", cancellationToken);
        }
    }

    private async Task SendMembershipApprovedAsync(MembershipApprovedMessage message, CancellationToken cancellationToken)
    {
        string organizationName = await dbContext.Organizations.AsNoTracking().Where(item => item.Id == message.OrganizationId).Select(item => item.Name).SingleAsync(cancellationToken);
        await DeliverMandatoryNotificationsAsync(message.UserId, message.OrganizationId, null, $"Membership approved: {organizationName}", $"Your representative membership for {organizationName} has been approved.", cancellationToken);
    }

    private async Task SendRequestSubmittedAsync(RequestSubmittedMessage message, CancellationToken cancellationToken)
    {
        var request = await dbContext.AssistanceRequests.AsNoTracking().Where(item => item.Id == message.RequestId)
            .Select(item => new { item.PublicReference, item.SubmittedByUserId, item.CreatedByUserId, item.OrganizationId }).SingleAsync(cancellationToken);
        Guid recipientUserId = request.SubmittedByUserId ?? request.CreatedByUserId;
        await DeliverMandatoryNotificationsAsync(recipientUserId, request.OrganizationId, message.RequestId, $"Request submitted: {request.PublicReference}", $"Your request {request.PublicReference} has been submitted to FFP for review.", cancellationToken);
    }

    private async Task SendRequestStatusChangedAsync(RequestStatusChangedMessage message, CancellationToken cancellationToken)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests.AsNoTracking().Include(item => item.StatusHistory).SingleAsync(item => item.Id == message.RequestId, cancellationToken);
        Guid recipientUserId = request.SubmittedByUserId ?? request.CreatedByUserId;
        string? reason = request.StatusHistory.OrderByDescending(item => item.ChangedUtc).Select(item => item.Reason).FirstOrDefault();
        string body = $"Request {request.PublicReference} is now {request.Status}.";
        if (!string.IsNullOrWhiteSpace(reason)) body += $"\n\n{reason}";
        await DeliverMandatoryNotificationsAsync(recipientUserId, request.OrganizationId, request.Id, $"Request status: {request.PublicReference}", body, cancellationToken);
    }

    private async Task SendFfpManagerInformationRequestedAsync(FfpManagerInformationRequestedMessage message, CancellationToken cancellationToken)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests.AsNoTracking().SingleAsync(item => item.Id == message.RequestId, cancellationToken);
        Guid recipientUserId = request.SubmittedByUserId ?? request.CreatedByUserId;
        string body = $"FFP needs additional information for request {request.PublicReference}.\n\n{message.Message}";
        if (message.DueUtc.HasValue) body += $"\n\nPlease respond by {message.DueUtc.Value:yyyy-MM-dd}.";
        body += "\n\nSign in to Request Portal to provide the requested information and supporting documents.";
        await DeliverMandatoryNotificationsAsync(recipientUserId, request.OrganizationId, request.Id, $"Information required: {request.PublicReference}", body, cancellationToken);
    }

    private async Task DeliverMandatoryNotificationsAsync(
        Guid userId,
        Guid? organizationId,
        Guid? requestId,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        await DeliverEmailAsync(
            userId,
            organizationId,
            requestId,
            subject,
            body,
            mandatory: true,
            cancellationToken: cancellationToken);

        await DeliverSupplementalChannelAsync(
            userId,
            organizationId,
            requestId,
            NotificationChannel.WhatsApp,
            NotificationTransport.WhatsApp,
            notificationOptions.WhatsAppEnabled,
            subject,
            body,
            cancellationToken);

        await DeliverSupplementalChannelAsync(
            userId,
            organizationId,
            requestId,
            NotificationChannel.Sms,
            NotificationTransport.Sms,
            notificationOptions.SmsEnabled,
            subject,
            BuildSmsBody(subject, body),
            cancellationToken);
    }

    private async Task DeliverSupplementalChannelAsync(
        Guid userId,
        Guid? organizationId,
        Guid? requestId,
        NotificationChannel channel,
        NotificationTransport transport,
        bool commissioned,
        string subject,
        string body,
        CancellationToken cancellationToken)
    {
        if (!commissioned)
        {
            return;
        }

        NotificationPreference? preference = await dbContext.NotificationPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.Channel == channel, cancellationToken);
        if (preference is null || !preference.Enabled || string.IsNullOrWhiteSpace(preference.Destination))
        {
            return;
        }

        NotificationHistory history = new(
            Guid.NewGuid(),
            userId,
            organizationId,
            requestId,
            channel,
            preference.Destination,
            subject,
            DateTimeOffset.UtcNow);
        dbContext.NotificationHistory.Add(history);

        try
        {
            await notificationSender.SendAsync(
                new NotificationEnvelope(preference.Destination, subject, body, false, transport),
                cancellationToken);
            history.MarkSent(DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            history.MarkFailed(exception.Message);
            throw;
        }
    }

    private async Task DeliverEmailAsync(
        Guid userId,
        Guid? organizationId,
        Guid? requestId,
        string subject,
        string body,
        bool mandatory,
        CancellationToken cancellationToken)
    {
        string? accountEmail = await dbContext.Users.AsNoTracking().Where(user => user.Id == userId).Select(user => user.Email).SingleOrDefaultAsync(cancellationToken);
        NotificationPreference? preference = await dbContext.NotificationPreferences.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId && item.Channel == NotificationChannel.Email, cancellationToken);
        string destination = preference?.Destination ?? accountEmail ?? "(no email address)";
        NotificationHistory history = new(Guid.NewGuid(), userId, organizationId, requestId, NotificationChannel.Email, destination, subject, DateTimeOffset.UtcNow);
        dbContext.NotificationHistory.Add(history);

        if (!mandatory && preference is not null && !preference.Enabled)
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
            await notificationSender.SendAsync(new NotificationEnvelope(destination, subject, body), cancellationToken);
            history.MarkSent(DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            history.MarkFailed(exception.Message);
            throw;
        }
    }

    private static string BuildSmsBody(string subject, string body)
    {
        string compact = $"{subject}. {body}".Replace("\r", " ").Replace("\n", " ");
        while (compact.Contains("  ", StringComparison.Ordinal))
        {
            compact = compact.Replace("  ", " ", StringComparison.Ordinal);
        }

        const int maxLength = 480;
        return compact.Length <= maxLength ? compact : compact[..(maxLength - 3)] + "...";
    }

    private static bool IsDue(OutboxMessage message, DateTimeOffset now)
    {
        if (message.AttemptCount <= 0) return true;
        if (TryGetRetryAt(message.LastError, out DateTimeOffset retryAt)) return now >= retryAt;
        return message.OccurredUtc.Add(GetRetryDelay(message.AttemptCount)) <= now;
    }

    private static TimeSpan GetRetryDelay(int attemptCount)
    {
        int exponent = Math.Min(Math.Max(attemptCount - 1, 0), 6);
        return TimeSpan.FromMinutes(Math.Pow(2, exponent));
    }

    private static bool TryGetRetryAt(string? lastError, out DateTimeOffset retryAt)
    {
        retryAt = default;
        if (string.IsNullOrWhiteSpace(lastError) || !lastError.StartsWith(RetryAtPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        int delimiter = lastError.IndexOf('|', RetryAtPrefix.Length);
        if (delimiter <= RetryAtPrefix.Length)
        {
            return false;
        }

        string value = lastError.Substring(RetryAtPrefix.Length, delimiter - RetryAtPrefix.Length);
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out retryAt);
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
            ?? throw new InvalidOperationException($"Unable to deserialize {typeof(TMessage).Name} outbox payload.");
    }
}
