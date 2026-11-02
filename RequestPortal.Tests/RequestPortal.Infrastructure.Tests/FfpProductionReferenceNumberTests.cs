using FfpManager.Bridge;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class FfpProductionReferenceNumberTests
{
    [Theory]
    [InlineData("KGN", 11293, "KGN/26/GR/11293")]
    [InlineData("CLA", 42, "CLA/26/GR/0042")]
    public void ReferenceRetainsParishYearAndSharedCounter(string parish, long counter, string expected) =>
        Assert.Equal(expected, FfpManagerReferenceNumberGenerator.Format(parish, new DateTimeOffset(2026,11,2,0,0,0,TimeSpan.Zero), counter));

    [Theory]
    [InlineData("", 42)]
    [InlineData("Kingston/Other", 42)]
    [InlineData("KGN", 0)]
    public void InvalidParishOrCounterCannotProduceReference(string parish, long counter) =>
        Assert.Throws<ArgumentException>(() => FfpManagerReferenceNumberGenerator.Format(parish, DateTimeOffset.UtcNow, counter));
}
