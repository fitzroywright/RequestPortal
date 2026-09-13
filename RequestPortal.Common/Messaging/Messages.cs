using System;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Common.Messaging;

public interface IMessage
{
    Guid MessageId { get; }

    DateTimeOffset OccurredUtc { get; }
}

public interface IMessagePublisher
{
    Task PublishAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
        where TMessage : IMessage;
}

public enum NotificationTransport
{
    Email = 0,
    Sms = 1,
    WhatsApp = 2
}

public sealed record NotificationEnvelope(
    string Recipient,
    string Subject,
    string Body,
    bool IsHtml = false,
    NotificationTransport Transport = NotificationTransport.Email);

public interface INotificationSender
{
    Task SendAsync(NotificationEnvelope notification, CancellationToken cancellationToken = default);
}

public sealed record OrganizationApprovedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid OrganizationId) : IMessage;

public sealed record OrganizationRegistrationReminderMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid OrganizationId,
    DateOnly ExpiryDate,
    string ReminderKind) : IMessage;

public sealed record OrganizationRegistrationVerifiedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid OrganizationId,
    Guid DocumentId) : IMessage;

public sealed record MembershipRequestedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid OrganizationId,
    Guid MembershipId,
    Guid RequestedByUserId) : IMessage;

public sealed record MembershipApprovedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid OrganizationId,
    Guid MembershipId,
    Guid UserId) : IMessage;

public sealed record RequestSubmittedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid RequestId,
    Guid OrganizationId) : IMessage;

public sealed record FfpManagerSubmissionRequestedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid RequestId) : IMessage;

public sealed record RequestStatusChangedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid RequestId,
    int Status) : IMessage;

public sealed record FfpManagerInformationRequestedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid RequestId,
    string Message,
    DateTimeOffset? DueUtc) : IMessage;
