using System;
using RequestPortal.Infrastructure.Identity;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class IndividualProfileCompletenessTests
{
    [Fact]
    public void NewExternalProfile_IsIncompleteUntilRequiredIntakeFieldsArePresent()
    {
        ApplicationUser user = new()
        {
            Id = Guid.NewGuid(),
            DisplayName = "Maria Brown",
            Email = "maria@example.org",
            PhoneNumber = "8765551234"
        };

        Assert.False(user.IsIndividualProfileComplete);
        Assert.Contains("date of birth", user.GetMissingIndividualProfileFields());
        Assert.Contains("national ID / TRN", user.GetMissingIndividualProfileFields());
        Assert.Contains("housing situation", user.GetMissingIndividualProfileFields());
    }

    [Fact]
    public void FullIntakeProfile_IsCompleteWithoutOptionalFields()
    {
        ApplicationUser user = new()
        {
            Id = Guid.NewGuid(),
            DisplayName = "Maria Brown",
            Email = "maria@example.org",
            PhoneNumber = "8765551234",
            DateOfBirth = new DateOnly(1985, 6, 15),
            Gender = "Female",
            NationalIdTrn = "123456789",
            MaritalStatus = "Single",
            HomeAddress = "1 Test Road",
            Parish = "St. Catherine",
            DistrictArea = "Spanish Town",
            EmploymentStatus = "Employed",
            MonthlyHouseholdIncome = 85000m,
            HouseholdSize = 4,
            NumberOfDependants = 2,
            ChildrenUnder18 = 2,
            HousingSituation = "Rent",
            HouseholdLivingSituation = "Four-person household in rented accommodation."
        };

        Assert.True(user.IsIndividualProfileComplete);
        Assert.Empty(user.GetMissingIndividualProfileFields());
    }
}
