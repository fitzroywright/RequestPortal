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
    public void ContradictoryOrUnknownOrMissingAnswers_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            QuickIntakeVictimCircumstances.Normalize(["None of the above", "Flooding"]));
        Assert.Throws<ArgumentException>(() =>
            QuickIntakeVictimCircumstances.Normalize(["Unknown"]));
        Assert.Throws<ArgumentException>(() => QuickIntakeVictimCircumstances.Normalize([]));
    }
}
