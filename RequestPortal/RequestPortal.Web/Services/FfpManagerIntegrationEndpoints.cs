using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Integration;

namespace RequestPortal.Web.Services;

public static class FfpManagerIntegrationEndpoints
{
    public static IEndpointRouteBuilder MapFfpManagerIntegrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/integration/ffpmanager");

        group.MapPost("/status", async (
            HttpRequest httpRequest,
            FfpManagerStatusCallback callback,
            IFfpManagerIntegrationService integrationService,
            IOptions<FfpManagerBridgeOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(httpRequest, options.Value.SharedSecret))
            {
                return Results.Unauthorized();
            }

            await integrationService.ReceiveStatusAsync(
                callback.MessageId,
                callback.PortalRequestId,
                callback.Status,
                cancellationToken);
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapPost("/information-requests", async (
            HttpRequest httpRequest,
            FfpManagerInformationRequest informationRequest,
            IFfpManagerIntegrationService integrationService,
            IOptions<FfpManagerBridgeOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!IsAuthorized(httpRequest, options.Value.SharedSecret))
            {
                return Results.Unauthorized();
            }

            await integrationService.ReceiveInformationRequestAsync(informationRequest, cancellationToken);
            return Results.NoContent();
        }).AllowAnonymous();

        return endpoints;
    }

    private static bool IsAuthorized(HttpRequest request, string? expectedSecret)
    {
        if (string.IsNullOrWhiteSpace(expectedSecret))
        {
            return false;
        }

        string supplied = request.Headers["X-FFP-Integration-Key"].ToString();
        byte[] expected = Encoding.UTF8.GetBytes(expectedSecret);
        byte[] actual = Encoding.UTF8.GetBytes(supplied);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
