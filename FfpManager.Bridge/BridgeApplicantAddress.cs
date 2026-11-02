using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public static class BridgeApplicantAddress
{
    public static void Validate(ExternalApplicationSubmission submission)
    {
        if (submission.StreetAddress?.Length > 500 || submission.CommunityDistrict?.Length > 500)
            throw new InvalidOperationException("FFP Manager Street and Town each permit at most 500 characters.");
    }
}
