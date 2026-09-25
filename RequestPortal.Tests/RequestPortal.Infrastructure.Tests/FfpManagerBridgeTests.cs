using System.Text;
using FfpManager.Bridge;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class FfpManagerBridgeTests
{
    [Fact]
    public async Task CreateApplicationAsync_IsIdempotentByCorrelationId()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeFfpManagerGateway gateway = new();
        FfpManagerApplicationSubmission submission = CreateSubmission(Guid.NewGuid());

        FfpManagerApplicationReference first = await gateway.CreateApplicationAsync(submission, cancellationToken);
        FfpManagerApplicationReference second = await gateway.CreateApplicationAsync(submission, cancellationToken);

        Assert.Equal(first.ApplicationId, second.ApplicationId);
        Assert.False(first.AlreadyExisted);
        Assert.True(second.AlreadyExisted);
    }

    [Fact]
    public async Task SetStatusAsync_ChangesStatusWithoutChangingApplicationIdentity()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeFfpManagerGateway gateway = new();
        FfpManagerApplicationReference reference = await gateway.CreateApplicationAsync(
            CreateSubmission(Guid.NewGuid()),
            cancellationToken);

        FfpManagerApplicationStatus changed = await gateway.SetStatusAsync(
            reference.ApplicationId,
            "APPROVED",
            "Approved",
            "Approved in FFP Manager test workflow.",
            terminal: false,
            cancellationToken: cancellationToken);
        FfpManagerApplicationStatus? current = await gateway.GetStatusAsync(reference.ApplicationId, cancellationToken);

        Assert.NotNull(current);
        Assert.Equal(reference.ApplicationId, changed.ApplicationId);
        Assert.Equal("APPROVED", current.StatusCode);
        Assert.Equal(changed, current);
    }

    [Fact]
    public async Task SaveArtifactAsync_TracksDocumentAgainstApplication()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeFfpManagerGateway gateway = new();
        FfpManagerApplicationReference reference = await gateway.CreateApplicationAsync(
            CreateSubmission(Guid.NewGuid()),
            cancellationToken);
        Guid documentId = Guid.NewGuid();
        byte[] bytes = Encoding.UTF8.GetBytes("sample supporting document");
        FfpManagerArtifactDescriptor descriptor = new(
            documentId,
            "RequestSupport",
            "quotation.txt",
            "text/plain",
            bytes.Length,
            DateTimeOffset.UtcNow);

        await using MemoryStream content = new(bytes);
        await gateway.SaveArtifactAsync(reference.ApplicationId, descriptor, content, cancellationToken);

        FakeFfpManagerApplicationSnapshot snapshot = gateway.GetSnapshot(reference.ApplicationId);
        FfpManagerArtifactDescriptor artifact = Assert.Single(snapshot.Artifacts);
        Assert.Equal(documentId, artifact.PortalDocumentId);
        Assert.Equal("quotation.txt", artifact.FileName);
    }

    [Fact]
    public async Task SaveArtifactAsync_IsIdempotentByPortalDocumentId()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeFfpManagerGateway gateway = new();
        FfpManagerApplicationReference reference = await gateway.CreateApplicationAsync(
            CreateSubmission(Guid.NewGuid()),
            cancellationToken);
        Guid documentId = Guid.NewGuid();
        FfpManagerArtifactDescriptor first = new(documentId, "RequestSupport", "first.txt", "text/plain", 3, DateTimeOffset.UtcNow);
        FfpManagerArtifactDescriptor replacement = first with { FileName = "replacement.txt" };

        await using (MemoryStream content = new(Encoding.UTF8.GetBytes("one")))
        {
            await gateway.SaveArtifactAsync(reference.ApplicationId, first, content, cancellationToken);
        }
        await using (MemoryStream content = new(Encoding.UTF8.GetBytes("two")))
        {
            await gateway.SaveArtifactAsync(reference.ApplicationId, replacement, content, cancellationToken);
        }

        FakeFfpManagerApplicationSnapshot snapshot = gateway.GetSnapshot(reference.ApplicationId);
        FfpManagerArtifactDescriptor artifact = Assert.Single(snapshot.Artifacts);
        Assert.Equal("replacement.txt", artifact.FileName);
    }

    [Fact]
    public async Task SaveInformationResponseAsync_PreservesResponseAndArtifacts()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FakeFfpManagerGateway gateway = new();
        FfpManagerApplicationSubmission submission = CreateSubmission(Guid.NewGuid());
        FfpManagerApplicationReference reference = await gateway.CreateApplicationAsync(submission, cancellationToken);
        FfpManagerArtifactDescriptor artifact = new(
            Guid.NewGuid(),
            "RequestSupport",
            "beneficiary-list.pdf",
            "application/pdf",
            100,
            DateTimeOffset.UtcNow);
        FfpManagerInformationResponse response = new(
            Guid.NewGuid(),
            submission.PortalRequestId,
            reference.ApplicationId,
            Guid.NewGuid(),
            "The requested information is attached.",
            DateTimeOffset.UtcNow,
            [artifact]);

        await gateway.SaveInformationResponseAsync(response, cancellationToken);

        FakeFfpManagerApplicationSnapshot snapshot = gateway.GetSnapshot(reference.ApplicationId);
        FfpManagerInformationResponse captured = Assert.Single(snapshot.Responses);
        Assert.Equal(response.MessageId, captured.MessageId);
        Assert.Equal(submission.PortalRequestId, captured.PortalRequestId);
        Assert.Single(captured.Artifacts);
    }

    [Fact]
    public void StatusCallbackIdentity_IsDeterministic()
    {
        DateTimeOffset changedUtc = new(2026, 9, 25, 15, 0, 0, TimeSpan.Zero);

        Guid first = ApplicationLetterCallbackIdentity.CreateStatusMessageId(
            "42",
            "APPROVED",
            changedUtc);
        Guid second = ApplicationLetterCallbackIdentity.CreateStatusMessageId(
            "42",
            "APPROVED",
            changedUtc);
        Guid changed = ApplicationLetterCallbackIdentity.CreateStatusMessageId(
            "42",
            "CLOSED",
            changedUtc);

        Assert.Equal(first, second);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public async Task SqlGateway_RequiresConnectionString()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        SqlFfpManagerGateway gateway = new(Options.Create(new BridgeOptions()));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.CreateApplicationAsync(CreateSubmission(Guid.NewGuid()), cancellationToken));

        Assert.Contains("SqlConnectionString", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FfpManagerApplicationSubmission CreateSubmission(Guid correlationId) => new(
        correlationId,
        Guid.NewGuid(),
        "RP-TEST-001",
        Guid.NewGuid(),
        "Test Organization",
        "REG-001",
        "Test address",
        "876-555-0100",
        "test@example.invalid",
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        "Standard",
        "Normal",
        "Test request",
        "General",
        null,
        null,
        "Jamaica",
        "Test circumstances",
        [new FfpManagerApplicationItem(Guid.NewGuid(), "Roofing material", 10, "EA")],
        []);
}
