using Microsoft.AspNetCore.Http.HttpResults;
using RequestPortal.Application.Integration;

namespace FfpManager.Bridge;

public static class ApplicationSubmissionEndpoint
{
    public static async Task<Results<Ok<ExternalApplicationReference>, ValidationProblem>> HandleAsync(
        ExternalApplicationSubmission submission,
        IFfpManagerGateway gateway,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await gateway.CreateApplicationAsync(submission, cancellationToken));
        }
        catch (ExternalApplicationValidationException exception)
        {
            loggerFactory.CreateLogger("FfpManager.Bridge.ApplicationSubmissionEndpoint").LogError(exception,
                "FFP Manager submission rejected for request {RequestId} at validation field {Field}",
                submission.PortalRequestId, exception.Field);
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [exception.Field] = [exception.Message]
            });
        }
    }
}
