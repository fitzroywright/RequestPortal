using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Identity;

namespace RequestPortal.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsInternalStaff { get; set; }
    public string? DirectoryUserName { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastSignInUtc { get; set; }
    public string? ProfilePhotoStorageKey { get; set; }
    public string? ProfilePhotoContentType { get; set; }
    public DateTimeOffset? ProfilePhotoUpdatedUtc { get; set; }

    // A representative account returns to individual mode when its final approved
    // organization membership ends. The profile must then be completed before
    // personal requests can be submitted.
    public bool IndividualProfileUpdateRequired { get; set; }
    public DateTimeOffset? IndividualProfileUpdateRequiredUtc { get; set; }

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool? HasChildrenUnder18LivingFullTime { get; set; }
    public bool? HasDisabilityOrSeriousMedicalCondition { get; set; }
    public string? AssistancePrograms { get; set; }
    public string? VictimCircumstances { get; set; }

    public DateOnly? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? NationalIdTrn { get; set; }
    public string? MaritalStatus { get; set; }
    public string? HomeAddress { get; set; }
    public string? HomeAddressLine2 { get; set; }
    public string? Town { get; set; }
    public string? Parish { get; set; }
    public string? DistrictArea { get; set; }
    public string? AlternativePhoneNumber { get; set; }
    public string? EmploymentStatus { get; set; }
    public string? EmployerInstitution { get; set; }
    public decimal? MonthlyHouseholdIncome { get; set; }
    public int? HouseholdSize { get; set; }
    public int? NumberOfDependants { get; set; }
    public int? ChildrenUnder18 { get; set; }
    public string? HousingSituation { get; set; }
    public string? HouseholdLivingSituation { get; set; }
    public string? ReferralSource { get; set; }

    public IReadOnlyList<string> GetMissingIndividualProfileFields()
    {
        List<string> missing = [];
        if (string.IsNullOrWhiteSpace(DisplayName)) missing.Add("name");
        if (string.IsNullOrWhiteSpace(Email)) missing.Add("email");
        if (string.IsNullOrWhiteSpace(PhoneNumber)) missing.Add("primary telephone");
        if (!DateOfBirth.HasValue) missing.Add("date of birth");
        if (string.IsNullOrWhiteSpace(Gender)) missing.Add("gender");
        if (string.IsNullOrWhiteSpace(NationalIdTrn)) missing.Add("national ID / TRN");
        if (string.IsNullOrWhiteSpace(MaritalStatus)) missing.Add("marital status");
        if (string.IsNullOrWhiteSpace(HomeAddress)) missing.Add("address line 1");
        if (string.IsNullOrWhiteSpace(Town)) missing.Add("town");
        if (string.IsNullOrWhiteSpace(Parish)) missing.Add("parish");
        if (string.IsNullOrWhiteSpace(DistrictArea)) missing.Add("district / area");
        if (string.IsNullOrWhiteSpace(EmploymentStatus)) missing.Add("employment status");
        if (!MonthlyHouseholdIncome.HasValue) missing.Add("monthly household income");
        if (!HouseholdSize.HasValue) missing.Add("persons in household");
        if (!NumberOfDependants.HasValue) missing.Add("dependants");
        if (!ChildrenUnder18.HasValue) missing.Add("children under 18");
        if (string.IsNullOrWhiteSpace(HousingSituation)) missing.Add("housing situation");
        if (string.IsNullOrWhiteSpace(HouseholdLivingSituation)) missing.Add("household / living situation");
        if (string.IsNullOrWhiteSpace(ReferralSource)) missing.Add("referral source");
        return missing;
    }

    public bool IsIndividualProfileComplete =>
        GetMissingIndividualProfileFields().Count == 0;
}
