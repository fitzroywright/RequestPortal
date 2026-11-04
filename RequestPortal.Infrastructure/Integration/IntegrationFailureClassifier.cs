using System.Net;
using RequestPortal.Application.Integration;

namespace RequestPortal.Infrastructure.Integration;

internal static class IntegrationFailureClassifier
{
    public static TransferFailure Classify(Exception exception, string stage) => exception switch
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
