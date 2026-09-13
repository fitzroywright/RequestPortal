using System;
using RequestPortal.Domain.Fulfilment;
using Xunit;

namespace RequestPortal.Domain.Tests;

public sealed class FulfilmentTests
{
    [Fact]
    public void StartIsIdempotentForSameReference()
    {
        Fulfilment fulfilment = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            FulfilmentSource.LocalIcp,
            DateTimeOffset.UtcNow);

        fulfilment.MarkInProgress("ICP-123");
        fulfilment.MarkInProgress("ICP-123");

        Assert.Equal(FulfilmentStatus.InProgress, fulfilment.Status);
        Assert.Equal("ICP-123", fulfilment.ExternalReference);
    }

    [Fact]
    public void CompletingFulfilmentTwiceIsIdempotent()
    {
        Fulfilment fulfilment = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            FulfilmentSource.FftpUs,
            DateTimeOffset.UtcNow);

        DateTimeOffset completedUtc = DateTimeOffset.UtcNow;
        fulfilment.Complete(completedUtc, "Completed");
        fulfilment.Complete(completedUtc.AddMinutes(1), "Ignored duplicate completion");

        Assert.Equal(FulfilmentStatus.Fulfilled, fulfilment.Status);
        Assert.Equal(completedUtc, fulfilment.FulfilledUtc);
        Assert.Equal("Completed", fulfilment.Notes);
    }

    [Fact]
    public void FulfilledFulfilmentCannotBeCancelled()
    {
        Fulfilment fulfilment = new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            FulfilmentSource.LocalIcp,
            DateTimeOffset.UtcNow);

        fulfilment.Complete(DateTimeOffset.UtcNow, null);

        Assert.Throws<InvalidOperationException>(() => fulfilment.Cancel("cancel"));
    }
}
