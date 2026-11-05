using Microsoft.AspNetCore.Http;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public static class ApplicationSubmissionEndpoint
{
    public static async Task<IResult> HandleAsync(
        ExternalApplicationSubmission submission,
        IFfpManagerGateway gateway,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await gateway.CreateApplicationAsync(submission, cancellationToken));
        }
        catch (ExternalApplicationValidationException exception)
        {
            loggerFactory.CreateLogger("FfpManager.Bridge.ApplicationSubmissionEndpoint").LogError(exception,
                "FFP Manager submission rejected for request {RequestId} at validation field {Field}",
                submission.PortalRequestId, exception.Field);
            string code = exception.Field switch {
                "PrimaryTelephone" => "VALIDATION_PRIMARY_TELEPHONE",
                "SecondaryTelephone" => "VALIDATION_SECONDARY_TELEPHONE",
                "Parish" => "VALIDATION_PARISH",
                _ => "VALIDATION"
            };
            string field = exception.Field switch {
                "PrimaryTelephone" => "Primary telephone",
                "SecondaryTelephone" => "Secondary telephone",
                "Parish" => "Parish",
                _ => "Submission field"
            };
            string reason = exception.Message == "The applicant telephone requires correction before transfer."
                ? "requires correction before transfer"
                : "was rejected by destination validation";
            var failure = TransferFailures.For("Validation", code) with {
                Explanation = $"{field} {reason}.",
                TechnicalDetails = IntegrationExceptionDiagnostics.Capture(exception, "Validation", code)
            };
            return Results.Json(failure, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
