using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Integration;

public static class FfpManagerStatusMapping
{
    public static RequestStatus? Map(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "PENDING INVESTIGATION" => RequestStatus.Submitted,
        "MORE INFORMATION REQUIRED" or "INCOMPLETE" => RequestStatus.MoreInformationRequired,
        "APPROVED" => RequestStatus.Approved,
        "UNABLE TO ASSIST" => RequestStatus.Rejected,
        "NO LONGER IN NEED" => RequestStatus.Cancelled,
        "COMPLETE" => RequestStatus.Fulfilled,
        "ON HOLD" => RequestStatus.OnHold,
        "INVESTIGATED" => RequestStatus.Investigated,
        "SENT FOR ASSESSMENT" => RequestStatus.SentForAssessment,
        _ => null
    };
}
