using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Identity;

public static class IntakeProfilePromotion
{
    public static void FillMissing(ApplicationUser user, QuickIntakeRecord intake)
    {
        if (string.IsNullOrWhiteSpace(user.DisplayName)) user.DisplayName = $"{intake.FirstName} {intake.LastName}";
        if (string.IsNullOrWhiteSpace(user.PhoneNumber)) user.PhoneNumber = intake.MobileNumber;
        if (string.IsNullOrWhiteSpace(user.Email) && !string.IsNullOrWhiteSpace(intake.Email))
        {
            user.Email = intake.Email;
            user.NormalizedEmail = intake.Email.ToUpperInvariant();
        }
        user.DateOfBirth ??= intake.DateOfBirth;
        if (string.IsNullOrWhiteSpace(user.Gender)) user.Gender = intake.Gender;
        if (string.IsNullOrWhiteSpace(user.MaritalStatus)) user.MaritalStatus = intake.MaritalStatus;
        if (string.IsNullOrWhiteSpace(user.HomeAddress)) user.HomeAddress = intake.StreetAddress;
        if (string.IsNullOrWhiteSpace(user.DistrictArea)) user.DistrictArea = intake.CommunityDistrict;
        if (string.IsNullOrWhiteSpace(user.Parish)) user.Parish = intake.Parish;
        if (string.IsNullOrWhiteSpace(user.Town)) user.Town = intake.CommunityDistrict;
        if (string.IsNullOrWhiteSpace(user.EmploymentStatus)) user.EmploymentStatus = intake.EmploymentStatus;
        user.MonthlyHouseholdIncome ??= intake.MonthlyIncome;
        if (string.IsNullOrWhiteSpace(user.FirstName)) user.FirstName = intake.FirstName;
        if (string.IsNullOrWhiteSpace(user.LastName)) user.LastName = intake.LastName;
        if (string.IsNullOrWhiteSpace(user.AlternativePhoneNumber)) user.AlternativePhoneNumber = intake.SecondaryTelephone;
        user.HasChildrenUnder18LivingFullTime ??= intake.HasChildrenUnder18LivingFullTime;
        user.HasDisabilityOrSeriousMedicalCondition ??= intake.HasDisabilityOrSeriousMedicalCondition;
        if (string.IsNullOrWhiteSpace(user.AssistancePrograms)) user.AssistancePrograms = intake.AssistancePrograms;
        if (string.IsNullOrWhiteSpace(user.VictimCircumstances)) user.VictimCircumstances = intake.VictimCircumstances;
        // A positive Yes/No answer does not establish a child count.
        if (!intake.HasChildrenUnder18LivingFullTime) user.ChildrenUnder18 ??= 0;
    }
}
