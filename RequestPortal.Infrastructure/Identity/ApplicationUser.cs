using System;
using Microsoft.AspNetCore.Identity;

namespace RequestPortal.Infrastructure.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsInternalStaff { get; set; }
    public string? DirectoryUserName { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastSignInUtc { get; set; }

    // A representative account returns to individual mode when its final approved
    // organization membership ends. The profile must then be completed before
    // personal requests can be submitted.
    public bool IndividualProfileUpdateRequired { get; set; }
    public DateTimeOffset? IndividualProfileUpdateRequiredUtc { get; set; }

    public DateOnly? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    public string? NationalIdTrn { get; set; }
    public string? MaritalStatus { get; set; }
    public string? HomeAddress { get; set; }
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
}
