using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace RequestPortal.Web.Messaging;

[Authorize(Policy = "PortalMessages")]
public sealed class PortalMessageHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        string? userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(userId))
        {
            await Groups.AddToGroupAsync(
                Context.ConnectionId,
                GroupName(userId));
        }

        await base.OnConnectedAsync();
    }

    public static string GroupName(string userId) => $"requestportal-user-{userId}";
}
