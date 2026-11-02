using RequestPortal.Domain.Integration;
using Xunit;

namespace RequestPortal.Domain.Tests;

public sealed class IntegrationLinkTests
{
    [Fact]
    public void SynchronizationKeepsApplicationIdentitySeparateFromItsBusinessReference()
    {
        var link = new IntegrationLink(Guid.NewGuid(), Guid.NewGuid(), "FFP Manager", DateTimeOffset.UtcNow);
        link.MarkSynchronized(" 123 ", DateTimeOffset.UtcNow, " KGN/26/GR/0042 ");
        Assert.Equal("123", link.ExternalReference);
        Assert.Equal("KGN/26/GR/0042", link.ApplicationNumber);
        link.MarkSynchronized("123", DateTimeOffset.UtcNow);
        Assert.Equal("KGN/26/GR/0042", link.ApplicationNumber);
    }
}
