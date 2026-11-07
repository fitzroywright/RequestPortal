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
            string code = exception.Code;
            var failure = TransferFailures.For("Validation", code) with {
                TechnicalDetails = IntegrationExceptionDiagnostics.Capture(exception, "Validation", code)
            };
            return Results.Json(failure, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
    }
}
