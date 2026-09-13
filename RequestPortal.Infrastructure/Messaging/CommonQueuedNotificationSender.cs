using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Common.Messaging;
using RequestPortal.Common.Messaging;

namespace RequestPortal.Infrastructure.Messaging;

public sealed class CommonQueuedNotificationSender : INotificationSender
{
    private readonly IExternalDeliveryQueue queue;

    public CommonQueuedNotificationSender(IExternalDeliveryQueue queue)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public async Task SendAsync(
        NotificationEnvelope notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Recipient);

        Guid notificationId = Guid.NewGuid();
        RecipientSnapshot recipient = new(
            notification.Recipient,
            notification.Recipient,
            notification.Recipient,
            null,
            null,
            MessageChannel.Smtp);
        MessageRequest request = new()
        {
            RecipientIds = [notification.Recipient],
            Title = notification.Subject,
            Body = notification.Body,
            Channels = MessageChannel.Smtp,
            Source = "RequestPortal",
            CorrelationId = notificationId.ToString("N"),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ContentType"] = notification.IsHtml ? "text/html" : "text/plain"
            }
        };
        ExternalDeliveryWorkItem workItem = new(
            notificationId,
            [recipient],
            MessageChannel.Smtp,
            request,
            null,
            "RequestPortal notification",
            DateTimeOffset.UtcNow);

        await queue.EnqueueAsync(workItem, cancellationToken);
    }
}
