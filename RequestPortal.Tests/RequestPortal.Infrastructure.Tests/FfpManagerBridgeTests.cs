using FfpManager.Bridge;
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
            cancellationToken);
        FfpManagerApplicationStatus? current = await gateway.GetStatusAsync(reference.ApplicationId, cancellationToken);

        Assert.NotNull(current);
        Assert.Equal(reference.ApplicationId, changed.ApplicationId);
        Assert.Equal("APPROVED", current.StatusCode);
        Assert.Equal(changed, current);
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
