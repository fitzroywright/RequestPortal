using Common.Messaging;
using Microsoft.Extensions.Configuration;
using RequestPortal.Common.Messaging;
using RequestPortal.Infrastructure.Messaging;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class CommonMessagingAdoptionTests
{
    [Fact]
    public async Task NotificationSender_Enqueues_CommonMessaging_SmtpWorkItem()
    {
        CancellationToken testCancellationToken = TestContext.Current.CancellationToken;
        InMemoryExternalDeliveryQueue queue = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        CommonQueuedNotificationSender sender = new(queue, configuration);

        await sender.SendAsync(
            new NotificationEnvelope(
                "person@example.com",
                "Request updated",
                "Your request changed."),
            testCancellationToken);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

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
