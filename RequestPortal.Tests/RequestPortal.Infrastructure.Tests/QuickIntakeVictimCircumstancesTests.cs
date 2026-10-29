using RequestPortal.Application.Requests;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class QuickIntakeVictimCircumstancesTests
{
    [Fact]
    public void MultipleCircumstances_ArePreservedAndDuplicatesRemoved()
    {
        Assert.Equal("House Fire; Flooding",
            QuickIntakeVictimCircumstances.Normalize(["Flooding", "House Fire", "house fire"]));
    }

    [Fact]
    public void None_IsAnExplicitAnswer()
    {
        Assert.Equal("None of the Above",
            QuickIntakeVictimCircumstances.Normalize(["None of the above"]));
    }

    [Fact]
    public void LegacyDomesticAbuse_MapsToNewAbuseOption()
    {
        Assert.Equal("Abuse (Physical, Emotional, Sexual, Verbal, or Substance)",
            QuickIntakeVictimCircumstances.Normalize(["Domestic Abuse"]));
    }

    [Fact]
    public void SocialPension_ReplacesSolidarityAndPreservesLegacyValues()
    {
        Assert.Contains("Social Pension", ProfileIntakeChoices.Programs);
        Assert.DoesNotContain("Solidarity Program", ProfileIntakeChoices.Programs);
        Assert.Equal("Social Pension", ProfileIntakeChoices.NormalizePrograms(["Solidarity Program"]));
        Assert.Equal(["Social Pension"], ProfileIntakeChoices.Parse("Solidarity Program", '|'));
    }

    [Fact]
    public void ContradictoryOrUnknownOrMissingAnswers_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            QuickIntakeVictimCircumstances.Normalize(["None of the above", "Flooding"]));
        Assert.Throws<ArgumentException>(() =>
            QuickIntakeVictimCircumstances.Normalize(["Unknown"]));
        Assert.Throws<ArgumentException>(() => QuickIntakeVictimCircumstances.Normalize([]));
    }
}
