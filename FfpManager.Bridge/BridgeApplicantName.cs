using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public static class BridgeApplicantName
{
    public static void Validate(ExternalApplicationSubmission submission)
    {
        if (submission.FirstName?.Length > 200 || submission.LastName?.Length > 100)
            throw new InvalidOperationException("FFP Manager permits at most 200 characters for FirstName and 100 for LastName.");
    }
}
