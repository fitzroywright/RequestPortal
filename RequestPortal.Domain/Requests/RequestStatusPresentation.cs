namespace RequestPortal.Domain.Requests;

public static class RequestStatusPresentation
{
    public static string Label(string status) => status switch
    {
        "Draft" => "Draft",
        "Submitted" => "Submitted",
        "MoreInformationRequired" => "More Information Required",
        "Approved" => "Approved",
        "Rejected" => "Rejected",
        "Cancelled" => "Cancelled",
        "PendingFulfilment" => "Pending Fulfilment",
        "Fulfilled" => "Fulfilled",
        "Closed" => "Closed",
        "Duplicate" => "Duplicate",
        "RepresentativeReviewRequired" => "Representative Review Required",
        _ => Humanize(status)
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

    private static string Humanize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        System.Text.StringBuilder builder = new(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (index > 0 &&
                char.IsUpper(current) &&
                !char.IsWhiteSpace(value[index - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
