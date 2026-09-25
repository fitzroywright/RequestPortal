using Microsoft.Extensions.Options;
using RequestPortal.Application.Delivery;
using RequestPortal.Infrastructure.Integration;

namespace RequestPortal.Web.Services;

public static class DeliveryEndpoints
{
    public static IEndpointRouteBuilder MapDeliveryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/integration/ffpmanager/deliveries");

        group.MapPost("/", async (
            HttpRequest request,
            CreateDeliveryCommand command,
            IDeliveryService service,
            IOptions<FfpManagerBridgeOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!FfpManagerEndpointAuthorization.IsAuthorized(request, options.Value.SharedSecret))
            {
                return Results.Unauthorized();
            }

            string publicBaseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
            CreateDeliveryResult result = await service.CreateAsync(
                command with { PublicBaseUrl = publicBaseUrl },
                cancellationToken);
            string url = $"/Delivery/{result.DeliveryId:D}?token={Uri.EscapeDataString(result.AccessToken)}";
            return Results.Ok(new
            {
                deliveryId = result.DeliveryId,
                accessToken = result.AccessToken,
                acknowledgementUrl = url,
                notificationQueued = result.NotificationQueued
            });
        }).AllowAnonymous();

        group.MapGet("/confirmations", async (
            HttpRequest request,
            IDeliveryService service,
            IOptions<FfpManagerBridgeOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!FfpManagerEndpointAuthorization.IsAuthorized(request, options.Value.SharedSecret))
            {
                return Results.Unauthorized();
            }

            IReadOnlyList<DeliveryConfirmation> confirmations =
                await service.GetUnsyncedConfirmationsAsync(cancellationToken);
            return Results.Ok(confirmations);
        }).AllowAnonymous();

        group.MapPost("/{deliveryId:guid}/synced", async (
            Guid deliveryId,
            HttpRequest request,
            IDeliveryService service,
            IOptions<FfpManagerBridgeOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!FfpManagerEndpointAuthorization.IsAuthorized(request, options.Value.SharedSecret))
            {
                return Results.Unauthorized();
            }

            await service.MarkSyncedAsync(deliveryId, cancellationToken);
            return Results.NoContent();
        }).AllowAnonymous();

        return endpoints;
    }
}
