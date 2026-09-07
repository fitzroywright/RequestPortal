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

public sealed record OrganizationApprovedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid OrganizationId) : IMessage;

public sealed record RequestSubmittedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid RequestId,
    Guid OrganizationId) : IMessage;

public sealed record RequestStatusChangedMessage(
    Guid MessageId,
    DateTimeOffset OccurredUtc,
    Guid RequestId,
    int Status) : IMessage;
