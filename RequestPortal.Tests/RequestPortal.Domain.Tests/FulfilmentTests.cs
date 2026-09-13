using System;
using RequestPortal.Domain.Fulfilment;
using Xunit;

namespace RequestPortal.Domain.Tests;

public sealed class FulfilmentTests
{
    [Fact]
    public void StartIsIdempotentForSameReference()
    {
        Fulfilment fulfilment = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), FulfilmentSource.LocalIcp, DateTimeOffset.UtcNow);
        fulfilment.MarkInProgress("ICP-123");
        fulfilment.MarkInProgress("ICP-123");
        Assert.Equal(FulfilmentStatus.InProgress, fulfilment.Status);
        Assert.Equal("ICP-123", fulfilment.ExternalReference);
    }

    [Fact]
    public void CompletingFulfilmentTwiceIsIdempotent()
    {
        Fulfilment fulfilment = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), FulfilmentSource.FftpUs, DateTimeOffset.UtcNow);
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
        Fulfilment fulfilment = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), FulfilmentSource.LocalIcp, DateTimeOffset.UtcNow);
        fulfilment.Complete(DateTimeOffset.UtcNow, null);
        Assert.Throws<InvalidOperationException>(() => fulfilment.Cancel("cancel"));
    }

    [Fact]
    public void PartialProgressTracksQuantityAndValue()
    {
        FulfilmentLine line = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10m);
        line.RecordProgress(4m, 125.50m, DateTimeOffset.UtcNow, "First delivery");
        Assert.Equal(4m, line.FulfilledQuantity);
        Assert.Equal(6m, line.RemainingQuantity);
        Assert.Equal(125.50m, line.FulfilledValue);
        Assert.False(line.IsComplete);

        line.RecordProgress(6m, 200m, DateTimeOffset.UtcNow, "Final delivery");
        Assert.True(line.IsComplete);
        Assert.Equal(325.50m, line.FulfilledValue);
    }

    [Fact]
    public void PartialProgressCannotExceedRequestedQuantity()
    {
        FulfilmentLine line = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 5m);
        line.RecordProgress(4m, 0m, DateTimeOffset.UtcNow, null);
        Assert.Throws<InvalidOperationException>(() => line.RecordProgress(2m, 0m, DateTimeOffset.UtcNow, null));
    }
}
