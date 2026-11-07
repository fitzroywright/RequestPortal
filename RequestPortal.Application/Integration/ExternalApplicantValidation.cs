using RequestPortal.Common.Contacts;

namespace RequestPortal.Application.Integration;

/// <summary>Shared, value-free checks before any destination connection is opened.</summary>
public static class ExternalApplicantValidation
{
    public static void Validate(ExternalApplicationSubmission submission)
    {
        // Keep the existing destination name/address limits as well as the profile checks.
        if (submission.FirstName?.Length > 200 || submission.LastName?.Length > 100 ||
            submission.StreetAddress?.Length > 500 || submission.CommunityDistrict?.Length > 500)
            throw new ExternalApplicationValidationException("ApplicantProfile", "Applicant name or address exceeds the destination field limits.");
        _ = Telephone(submission.PrimaryTelephone, "PrimaryTelephone");
        _ = Telephone(submission.SecondaryTelephone, "SecondaryTelephone");
        DateOfBirth(submission.DateOfBirth);
        Email(submission.ApplicantEmail);
        Employment(submission.ApplicantEmploymentStatus);
        QuickIntakeApplicationFields.Validate(submission);
    }

    public static string? Telephone(string? value, string field)
    {
        try { return ApplicantTelephone.NormalizeNew(value); }
        catch (ArgumentException)
        { throw new ExternalApplicationValidationException(field, "The applicant telephone requires correction before transfer."); }
    }

    public static void DateOfBirth(DateOnly? date)
    {
        if (date is { } value && (value < new DateOnly(1753, 1, 1) || value > DateOnly.FromDateTime(DateTime.UtcNow)))
            throw new ExternalApplicationValidationException("DateOfBirth", "Date of birth must be a real date from January 1, 1753 through today.");
    }

    public static void Email(string? value)
    {
        try
        {
            if (value?.Trim().Length > 100 || ContactEmail.Normalize(value)?.Length > 100)
                throw new ArgumentException();
        }
        catch (ArgumentException)
        { throw new ExternalApplicationValidationException("ApplicantEmail", "Applicant email must be a valid email address of at most 100 characters."); }
    }

    public static void Employment(string? value)
    {
        // The destination also accepts historical free-text occupations; do not replace them with intake options.
        if (value?.Trim().Length > 255 || value?.Any(char.IsControl) == true)
            throw new ExternalApplicationValidationException("ApplicantEmploymentStatus", "Employment status must contain at most 255 characters and no control characters.");
    }
}
