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
    [Fact]
    public void ApplicationOnlyRecoveryNeverMarksDocumentsAsTransferred()
    {
        var now = DateTimeOffset.UtcNow;
        var link = new IntegrationLink(Guid.NewGuid(), Guid.NewGuid(), "FFPManager", now);
        link.BeginAttempt(now);
        link.RecordFailure(now, "Artifacts", "Artifacts", "ARTIFACT",
            "Supporting documents could not be transferred.", false);
        link.RecordVerifiedDestination("123", "KGN/26/GR/0042", "JMDB01", "FFPJM", now);
        link.MarkApplicationSynchronizedWithoutArtifacts("123", "KGN/26/GR/0042", now);
        Assert.Equal(SynchronizationStatus.Synchronized, link.Status);
        Assert.Equal("123", link.ExternalReference);
        Assert.Equal("KGN/26/GR/0042", link.ApplicationNumber);
        Assert.NotNull(link.VerifiedUtc);
        Assert.NotNull(link.SynchronizedUtc);
        Assert.Null(link.ArtifactsSynchronizedUtc);
        Assert.Equal("DocumentsOmitted", link.FailureClassification);
        Assert.Null(link.ErrorCode);
    }

}
