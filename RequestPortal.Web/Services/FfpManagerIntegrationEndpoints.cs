using System.Security.Cryptography;
using System.Text;
using Common.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Services;

public static class FfpManagerIntegrationEndpoints
{
    public static IEndpointRouteBuilder MapFfpManagerIntegrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/integration/ffpmanager");

        group.MapPost("/status", async (
            HttpRequest httpRequest,
            ExternalStatusCallback callback,
            IExternalApplicationIntegrationService integrationService,
            RequestPortalDbContext dbContext,
            IOptions<FfpManagerBridgeOptions> options,
            ISecretProvider secrets,
            CancellationToken cancellationToken) =>
        {
            FfpManagerBridgeOptions current = options.Value;
            string? expectedSecret = await ResolveSharedSecretAsync(current, secrets, cancellationToken);
            if (!IsAuthorized(httpRequest, expectedSecret))
            {
                return Results.Unauthorized();
            }

            if (!await MatchesIntegrationLinkAsync(
                    dbContext,
                    callback.PortalRequestId,
                    callback.Status.ApplicationId,
                    current.SystemName,
                    cancellationToken))
            {
                return Results.Conflict(new { error = "FFP Manager application identity does not match the RequestPortal integration link." });
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
            ExternalInformationRequest informationRequest,
            IExternalApplicationIntegrationService integrationService,
            RequestPortalDbContext dbContext,
            IOptions<FfpManagerBridgeOptions> options,
            ISecretProvider secrets,
            CancellationToken cancellationToken) =>
        {
            FfpManagerBridgeOptions current = options.Value;
            string? expectedSecret = await ResolveSharedSecretAsync(current, secrets, cancellationToken);
            if (!IsAuthorized(httpRequest, expectedSecret))
            {
                return Results.Unauthorized();
            }

            if (!await MatchesIntegrationLinkAsync(
                    dbContext,
                    informationRequest.PortalRequestId,
                    informationRequest.ApplicationId,
                    current.SystemName,
                    cancellationToken))
            {
                return Results.Conflict(new { error = "FFP Manager application identity does not match the RequestPortal integration link." });
            }

            await integrationService.ReceiveInformationRequestAsync(informationRequest, cancellationToken);
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapGet("/probe", async (
            HttpRequest httpRequest,
            IOptions<FfpManagerBridgeOptions> options,
            ISecretProvider secrets,
            CancellationToken cancellationToken) =>
        {
            FfpManagerBridgeOptions current = options.Value;
            string? expectedSecret = await ResolveSharedSecretAsync(current, secrets, cancellationToken);
            if (!IsAuthorized(httpRequest, expectedSecret))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                status = "ok",
                systemName = current.SystemName
            });
        }).AllowAnonymous();

        return endpoints;
    }

    private static async Task<string?> ResolveSharedSecretAsync(
        FfpManagerBridgeOptions options,
        ISecretProvider secrets,
        CancellationToken cancellationToken)
    {
        string secretName = string.IsNullOrWhiteSpace(options.SharedSecretName)
            ? "FfpManagerIntegration:SharedSecret"
            : options.SharedSecretName;

        string? secret = await secrets.GetAsync(secretName, cancellationToken);
        return string.IsNullOrWhiteSpace(secret)
            ? options.SharedSecret
            : secret;
    }

    private static Task<bool> MatchesIntegrationLinkAsync(
        RequestPortalDbContext dbContext,
        Guid requestId,
        string applicationId,
        string systemName,
        CancellationToken cancellationToken)
    {
        return dbContext.IntegrationLinks
            .AsNoTracking()
            .AnyAsync(
                link =>
                    link.RequestId == requestId &&
                    link.SystemName == systemName &&
                    link.ExternalReference == applicationId,
                cancellationToken);
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
