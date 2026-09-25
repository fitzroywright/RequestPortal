using Microsoft.AspNetCore.SignalR;
using RequestPortal.Application.Messages;

namespace RequestPortal.Web.Messaging;

public sealed class SignalRPortalMessageRealtimeNotifier(
    IHubContext<PortalMessageHub> hubContext) : IPortalMessageRealtimeNotifier
{
    public async Task NotifyAsync(
        PortalMessageCreated message,
        CancellationToken cancellationToken = default)
    {
        foreach (Guid userId in message.RecipientUserIds.Distinct())
        {
            await hubContext.Clients
                .Group(PortalMessageHub.GroupName(userId.ToString()))
                .SendAsync(
                    "messageReceived",
                    new
                    {
                        message.MessageId,
                        message.ThreadId,
                        message.Subject,
                        message.SenderName,
                        message.Preview
                    },
                    cancellationToken);
        }
    }
}
