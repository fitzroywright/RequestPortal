using System.Net;
using RequestPortal.Application.Integration;

namespace RequestPortal.Infrastructure.Integration;

internal static class IntegrationFailureClassifier
{
    // Persist the encountered exception type and an operator-safe, bounded description.
    // Raw exception.ToString(), SQL text, URLs, request payloads and inner exceptions
    // can contain credentials, applicant information or document content.
    public static TransferFailure Classify(Exception exception, string stage)
    {
        TransferFailure failure = ClassifyCategory(exception, stage);
        if (exception is IntegrationTransferException) return failure; // Bridge sends classified, safe details.
        string type = exception.GetType().Name;
        string observed = exception switch
        {
            HttpRequestException http when http.StatusCode.HasValue
                => $"HTTP {(int)http.StatusCode.Value} ({http.StatusCode.Value}) from the integration destination.",
            TimeoutException => "The integration call timed out.",
            TaskCanceledException => "The integration operation was cancelled or timed out.",
            OperationCanceledException => "The integration operation was cancelled.",
            ExternalApplicationValidationException validation
                => $"Invalid field: {validation.Field}.",
            InvalidOperationException when stage == "Preparation"
                => "The source request or required mapping failed validation.",
            _ => "The exception message is withheld because it may contain confidential values."
        };
        return failure with
        {
            Explanation = $"{failure.Explanation} Encountered {type} during {stage}: {observed}"
        };
    }

    private static TransferFailure ClassifyCategory(Exception exception, string stage) => exception switch
    {
        IntegrationTransferException transfer => transfer.Failure with { Stage = stage },
        ExternalApplicationValidationException { Field: "Parish" } => TransferFailures.For(stage, "VALIDATION_PARISH"),
        ExternalApplicationValidationException => TransferFailures.For(stage, "VALIDATION"),
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => TransferFailures.For(stage, "BRIDGE_AUTH"),
        HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity } => TransferFailures.For(stage, "VALIDATION"),
        HttpRequestException { StatusCode: HttpStatusCode.Conflict } => TransferFailures.For(stage, "DESTINATION_CONFLICT"),
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => TransferFailures.For(stage, "CONFIGURATION"),
        HttpRequestException or TimeoutException or OperationCanceledException => TransferFailures.For(stage, "CONNECTION"),
        InvalidOperationException when stage == "Preparation" => TransferFailures.For(stage, "VALIDATION"),
        NotSupportedException => TransferFailures.For(stage, "CONFIGURATION"),
        _ when stage == "Artifacts" => TransferFailures.For(stage, "ARTIFACT"),
        _ => TransferFailures.For(stage, "UNCLASSIFIED")
    };
}
