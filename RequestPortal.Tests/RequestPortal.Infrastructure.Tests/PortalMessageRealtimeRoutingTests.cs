using System.Reflection;
using Microsoft.AspNetCore.SignalR;
using RequestPortal.Application.Messages;
using RequestPortal.Web.Messaging;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class PortalMessageRealtimeRoutingTests
{
    [Fact]
    public async Task NotifierUsesOnlyDistinctExplicitUserGroupsAndNeverBroadcasts()
    {
        var clients = DispatchProxy.Create<IHubClients, GroupOnlyClients>();
        var recorder = (GroupOnlyClients)(object)clients;
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var message = new PortalMessageCreated(Guid.NewGuid(), Guid.NewGuid(), [first, first, second], "Fixture", "Fixture", "Fixture");
        await new SignalRPortalMessageRealtimeNotifier(new Context(clients)).NotifyAsync(message, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { PortalMessageHub.GroupName(first.ToString()), PortalMessageHub.GroupName(second.ToString()) }, recorder.Groups);
        Assert.Equal(2, recorder.Sends);
    }
    sealed class Context(IHubClients clients) : IHubContext<PortalMessageHub>
    {
        public IHubClients Clients => clients;
        public IGroupManager Groups => throw new InvalidOperationException("Notification dispatch must not change group membership.");
    }
    public class GroupOnlyClients : DispatchProxy
    {
        public List<string> Groups = [];
        public int Sends;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal("Group", method!.Name); // All, AllExcept, etc. fail here.
            Groups.Add((string)args![0]!);
            return new Client(this);
        }
        sealed class Client(GroupOnlyClients recorder) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
            { Assert.Equal("messageReceived", method); recorder.Sends++; return Task.CompletedTask; }
        }
    }
}
