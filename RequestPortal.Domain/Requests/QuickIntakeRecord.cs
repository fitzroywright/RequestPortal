using System;

namespace RequestPortal.Domain.Requests;

public sealed class QuickIntakeRecord
{
    private QuickIntakeRecord() { }

    public QuickIntakeRecord(
        Guid id,
        Guid requestId,
        Guid anonymousUserId,
        Guid individualContextId,
        string firstName,
        string lastName,
        DateOnly dateOfBirth,
        string gender,
        string maritalStatus,
        string mobileNumber,
        string? email,
        string streetAddress,
        string communityDistrict,
        string parish,
        string employmentStatus,
        decimal monthlyIncome,
        bool hasChildrenUnder18LivingFullTime,
        bool hasDisabilityOrSeriousMedicalCondition,
        string assistancePrograms,
        string helpCategory,
        DateTimeOffset createdUtc,
        string? secondaryTelephone = null,
        string? victimCircumstances = null,
        string? originalTelephone = null,
        string? originalSecondaryTelephone = null,
        int? payFrequency = QuickIntakeTransferDefaults.PayFrequency,
        int? phoneLineType = QuickIntakeTransferDefaults.PhoneLineType)
    {
        if (id == Guid.Empty) throw new ArgumentException("Quick intake id is required.", nameof(id));
        if (requestId == Guid.Empty) throw new ArgumentException("Request id is required.", nameof(requestId));
        if (anonymousUserId == Guid.Empty) throw new ArgumentException("Anonymous user id is required.", nameof(anonymousUserId));
        if (individualContextId == Guid.Empty) throw new ArgumentException("Individual context id is required.", nameof(individualContextId));
        if (string.IsNullOrWhiteSpace(firstName)) throw new ArgumentException("First name is required.", nameof(firstName));
        if (string.IsNullOrWhiteSpace(lastName)) throw new ArgumentException("Last name is required.", nameof(lastName));
        if (string.IsNullOrWhiteSpace(gender)) throw new ArgumentException("Gender is required.", nameof(gender));
        if (string.IsNullOrWhiteSpace(maritalStatus)) throw new ArgumentException("Marital status is required.", nameof(maritalStatus));
        if (string.IsNullOrWhiteSpace(mobileNumber)) throw new ArgumentException("Telephone is required.", nameof(mobileNumber));
        if (string.IsNullOrWhiteSpace(streetAddress)) throw new ArgumentException("Street address is required.", nameof(streetAddress));
        if (string.IsNullOrWhiteSpace(communityDistrict)) throw new ArgumentException("Community/District is required.", nameof(communityDistrict));
        if (string.IsNullOrWhiteSpace(parish)) throw new ArgumentException("Parish is required.", nameof(parish));
        if (string.IsNullOrWhiteSpace(employmentStatus)) throw new ArgumentException("Employment status is required.", nameof(employmentStatus));
        if (monthlyIncome < 0) throw new ArgumentOutOfRangeException(nameof(monthlyIncome));
        if (string.IsNullOrWhiteSpace(assistancePrograms)) throw new ArgumentException("Assistance program response is required.", nameof(assistancePrograms));
        if (string.IsNullOrWhiteSpace(helpCategory)) throw new ArgumentException("Help category is required.", nameof(helpCategory));

        Id = id;
        RequestId = requestId;
        AnonymousUserId = anonymousUserId;
        IndividualContextId = individualContextId;
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        DateOfBirth = dateOfBirth;
        Gender = gender.Trim();
        MaritalStatus = maritalStatus.Trim();
        MobileNumber = mobileNumber.Trim();
        OriginalTelephone = originalTelephone;
        OriginalSecondaryTelephone = originalSecondaryTelephone;
        SecondaryTelephone = Normalize(secondaryTelephone);
        VictimCircumstances = Normalize(victimCircumstances);
        Email = Normalize(email);
        StreetAddress = streetAddress.Trim();
        CommunityDistrict = communityDistrict.Trim();
        Parish = parish.Trim();
        EmploymentStatus = employmentStatus.Trim();
        MonthlyIncome = monthlyIncome;
        HasChildrenUnder18LivingFullTime = hasChildrenUnder18LivingFullTime;
        HasDisabilityOrSeriousMedicalCondition = hasDisabilityOrSeriousMedicalCondition;
        AssistancePrograms = assistancePrograms.Trim();
        HelpCategory = helpCategory.Trim();
        CreatedUtc = createdUtc;
        PayFrequency = payFrequency;
        PhoneLineType = phoneLineType;
    }

    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public Guid AnonymousUserId { get; private set; }
    public Guid IndividualContextId { get; private set; }
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public DateOnly DateOfBirth { get; private set; }
    public string Gender { get; private set; } = string.Empty;
    public string MaritalStatus { get; private set; } = string.Empty;
    public string MobileNumber { get; private set; } = string.Empty;
    // Source values are retained privately; historical originals cannot be reconstructed.
    public string? OriginalTelephone { get; private set; }
    public string? OriginalSecondaryTelephone { get; private set; }
    public string? VictimCircumstances { get; private set; }
    public string? SecondaryTelephone { get; private set; }
    public string? Email { get; private set; }
    public string StreetAddress { get; private set; } = string.Empty;
    public string CommunityDistrict { get; private set; } = string.Empty;
    public string Parish { get; private set; } = string.Empty;
    public string EmploymentStatus { get; private set; } = string.Empty;
    public decimal MonthlyIncome { get; private set; }
    public int? PayFrequency { get; private set; }
    public int? PhoneLineType { get; private set; }
    public bool HasChildrenUnder18LivingFullTime { get; private set; }
    public bool HasDisabilityOrSeriousMedicalCondition { get; private set; }
    public string AssistancePrograms { get; private set; } = string.Empty;
    public string HelpCategory { get; private set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; private set; }
    public Guid? ConvertedToUserId { get; private set; }
    public Guid? ConvertedByStaffUserId { get; private set; }
    public DateTimeOffset? ConvertedUtc { get; private set; }

    public bool IsConverted => ConvertedToUserId.HasValue;

    public void MarkConverted(Guid userId, Guid staffUserId, DateTimeOffset convertedUtc)
    {
        if (IsConverted) throw new InvalidOperationException("Quick intake has already been converted.");
        ConvertedToUserId = userId;
        ConvertedByStaffUserId = staffUserId;
        ConvertedUtc = convertedUtc;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
