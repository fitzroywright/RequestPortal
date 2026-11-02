using RequestPortal.Application.Integration;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Integration;

public static class FfpManagerSubmissionParish
{
    public static string Resolve(
        string publicReference, QuickIntakeRecord? intake, string? organizationType,
        string? organizationParish, string? applicantParish)
    {
        // The intake answer belongs to this request. A missing answer must not be
        // replaced by a different parish from a profile, address or free-text location.
        bool individual = organizationType == "IndividualContext";
        string? parish = intake is not null ? intake.Parish : individual ? applicantParish : organizationParish;
        if (string.IsNullOrWhiteSpace(parish))
        {
            string source = intake is not null ? "Quick Intake record" : individual ? "applicant profile" : "organization profile";
            throw new ExternalApplicationValidationException(nameof(ExternalApplicationSubmission.Parish),
                $"Request {publicReference} requires an explicitly selected parish in its {source} before FFP Manager submission can proceed. Location and address text cannot be used.");
        }
        return parish.Trim();
    }
}
