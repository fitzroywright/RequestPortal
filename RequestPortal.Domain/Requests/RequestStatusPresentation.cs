namespace RequestPortal.Domain.Requests;

public static class RequestStatusPresentation
{
    public static string Label(string status) => status switch
    {
        "Draft" => "Draft",
        "Submitted" => "Under review",
        "MoreInformationRequired" => "Information required — waiting on you",
        "Approved" => "Approved",
        "Rejected" => "Not approved",
        "PendingFulfilment" => "Preparing assistance",
        "Fulfilled" => "Assistance provided",
        "Closed" => "Completed",
        "Cancelled" => "Cancelled",
        "Duplicate" => "Duplicate request",
        _ => "Processing"
    };

    public static string Next(string status) => status switch
    {
        "Draft" => "Submit your request",
        "Submitted" => "FFP review and decision",
        "MoreInformationRequired" => "Provide the requested information",
        "Approved" => "FFP arranges assistance",
        "PendingFulfilment" => "Assistance delivery",
        "Fulfilled" => "FFP completes the request",
        _ => "No further action"
    };
}
