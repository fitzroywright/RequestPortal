using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Common.Messaging;
using Microsoft.Extensions.Configuration;
using RequestPortal.Common.Messaging;

namespace RequestPortal.Infrastructure.Messaging;

public sealed class CommonQueuedNotificationSender : RequestPortal.Common.Messaging.INotificationSender
{
    private readonly IExternalDeliveryQueue queue;
    private readonly IConfiguration configuration;

    public CommonQueuedNotificationSender(IExternalDeliveryQueue queue, IConfiguration configuration)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public async Task SendAsync(
        NotificationEnvelope notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentException.ThrowIfNullOrWhiteSpace(notification.Recipient);

        if(notification.Transport==NotificationTransport.Email)
        {
            notification=notification with {Recipient=RequestPortal.Common.Contacts.ContactEmail.Normalize(notification.Recipient)
                ?? throw new ArgumentException("Email recipient is required.")};
            if(notification.Subject.Any(char.IsControl))throw new ArgumentException("Email subject contains control characters.");
        }

        bool graphEmailEnabled = configuration.GetValue("Messaging:MicrosoftGraph:Email:Enabled", false);
        MessageChannel channel = notification.Transport switch
        {
            NotificationTransport.Email => graphEmailEnabled ? MessageChannel.MsEmail : MessageChannel.Smtp,
            NotificationTransport.WhatsApp => MessageChannel.WhatsApp,
            _ => throw new InvalidOperationException($"Unsupported notification transport '{notification.Transport}'.")
        };

        Guid notificationId = notification.DeliveryId ?? Guid.NewGuid();
        bool usesMobile = channel == MessageChannel.WhatsApp;
        RecipientSnapshot recipient = new(
            notification.Recipient,
            notification.Recipient,
            usesMobile ? null : notification.Recipient,
            null,
            usesMobile ? notification.Recipient : null,
            channel);
        Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ContentType"] = notification.IsHtml ? "text/html" : "text/plain",
            ["Transport"] = notification.Transport.ToString()
        };
        if (channel == MessageChannel.WhatsApp && !string.IsNullOrWhiteSpace(notification.WhatsAppTemplateName))
        {
            metadata["WhatsAppTemplateName"] = notification.WhatsAppTemplateName.Trim();
            metadata["WhatsAppTemplateLanguage"] = string.IsNullOrWhiteSpace(notification.WhatsAppTemplateLanguage)
                ? "en_US"
                : notification.WhatsAppTemplateLanguage.Trim();
            if (notification.WhatsAppTemplateParameters is { Count: > 0 })
            {
                metadata["WhatsAppTemplateParameters"] = string.Join("|", notification.WhatsAppTemplateParameters);
            }
        }

        MessageRequest request = new()
        {
            RecipientIds = [notification.Recipient],
            Title = notification.Subject,
            Body = notification.Body,
            Channels = channel,
            Source = "RequestPortal",
            CorrelationId = string.IsNullOrWhiteSpace(notification.CorrelationId)
                ? notificationId.ToString("N")
                : notification.CorrelationId.Trim(),
            Metadata = metadata
        };
        ExternalDeliveryWorkItem workItem = new(
            notificationId,
            [recipient],
            channel,
            request,
            request.CorrelationId,
            "RequestPortal notification",
            DateTimeOffset.UtcNow);

        await queue.EnqueueAsync(workItem, cancellationToken);
    }
}
