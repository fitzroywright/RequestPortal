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

        MessageChannel channel = notification.Transport switch
        {
            NotificationTransport.Email => MessageChannel.Smtp,
            NotificationTransport.Sms => MessageChannel.Sms,
            NotificationTransport.WhatsApp => MessageChannel.WhatsApp,
            _ => throw new InvalidOperationException($"Unsupported notification transport '{notification.Transport}'.")
        };

        Guid notificationId = Guid.NewGuid();
        bool usesMobile = channel is MessageChannel.Sms or MessageChannel.WhatsApp;
        RecipientSnapshot recipient = new(
            notification.Recipient,
            notification.Recipient,
            usesMobile ? null : notification.Recipient,
            null,
            usesMobile ? notification.Recipient : null,
            channel);
        MessageRequest request = new()
        {
            RecipientIds = [notification.Recipient],
            Title = notification.Subject,
            Body = notification.Body,
            Channels = channel,
            Source = "RequestPortal",
            CorrelationId = notificationId.ToString("N"),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ContentType"] = notification.IsHtml ? "text/html" : "text/plain",
                ["Transport"] = notification.Transport.ToString()
            }
        };
        ExternalDeliveryWorkItem workItem = new(
            notificationId,
            [recipient],
            channel,
            request,
            null,
            "RequestPortal notification",
            DateTimeOffset.UtcNow);

        await queue.EnqueueAsync(workItem, cancellationToken);
    }
}
