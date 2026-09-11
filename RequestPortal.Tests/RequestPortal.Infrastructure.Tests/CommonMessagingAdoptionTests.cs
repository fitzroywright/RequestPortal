using Common.Messaging;
using RequestPortal.Common.Messaging;
using RequestPortal.Infrastructure.Messaging;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class CommonMessagingAdoptionTests
{
    [Fact]
    public async Task NotificationSender_Enqueues_CommonMessaging_SmtpWorkItem()
    {
        InMemoryExternalDeliveryQueue queue = new();
        CommonQueuedNotificationSender sender = new(queue);

        await sender.SendAsync(new NotificationEnvelope(
            "person@example.com",
            "Request updated",
            "Your request changed."));

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        await foreach (ExternalDeliveryWorkItem item in queue.ReadAllAsync(timeout.Token))
        {
            Assert.Equal(MessageChannel.Smtp, item.RequestedChannels);
            Assert.Equal("RequestPortal", item.Request.Source);
            Assert.Single(item.Recipients);
            Assert.Equal("person@example.com", item.Recipients[0].Email);
            Assert.Equal("Request updated", item.Request.Title);
            Assert.Equal("Your request changed.", item.Request.Body);
            return;
        }

        Assert.Fail("No Common.Messaging work item was enqueued.");
    }
}
