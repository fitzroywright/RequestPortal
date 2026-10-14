using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class IntakeProfilePromotionTests
{
    private static QuickIntakeRecord Intake(bool children) => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "Jane", "Doe", new DateOnly(1980, 1, 1), "Female", "Single",
        "+18765550123", "jane@example.com", "1 Main Road", "Kingston", "Kingston",
        "Unemployed", 5000, children, true, "PATH|CARE", "Food",
        DateTimeOffset.UtcNow, "+18765550124", "House Fire; Flooding");

    [Fact]
    public void Promotion_CopiesApplicantFields_WithoutInventingPositiveChildCount()
    {
        ApplicationUser user = new();
        IntakeProfilePromotion.FillMissing(user, Intake(true));
        Assert.Equal("Jane", user.FirstName);
        Assert.Equal("Doe", user.LastName);
        Assert.Equal("+18765550124", user.AlternativePhoneNumber);
        Assert.True(user.HasChildrenUnder18LivingFullTime);
        Assert.Null(user.ChildrenUnder18);
        Assert.True(user.HasDisabilityOrSeriousMedicalCondition);
        Assert.Equal("PATH|CARE", user.AssistancePrograms);
        Assert.Equal("House Fire; Flooding", user.VictimCircumstances);
        Assert.Equal("Jane Doe", user.DisplayName);
        Assert.Equal("jane@example.com", user.Email);
    }

    [Fact]
    public void Promotion_PreservesExistingValues_IncludingExplicitFalse()
    {
        ApplicationUser user = new()
        {
            FirstName = "Existing", AlternativePhoneNumber = "+18765550000",
            HasChildrenUnder18LivingFullTime = false, ChildrenUnder18 = 3,
            HasDisabilityOrSeriousMedicalCondition = false,
            AssistancePrograms = "None", VictimCircumstances = "None of the above"
        };
        IntakeProfilePromotion.FillMissing(user, Intake(true));
        Assert.Equal("Existing", user.FirstName);
        Assert.Equal("+18765550000", user.AlternativePhoneNumber);
        Assert.False(user.HasChildrenUnder18LivingFullTime);
        Assert.Equal(3, user.ChildrenUnder18);
        Assert.False(user.HasDisabilityOrSeriousMedicalCondition);
        Assert.Equal("None", user.AssistancePrograms);
        Assert.Equal("None of the above", user.VictimCircumstances);
    }
}
