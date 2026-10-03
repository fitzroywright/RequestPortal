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
                return Results.Unauthorized();

            string publicBaseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
            CreateDeliveryResult result = await service.CreateAsync(
                command with { PublicBaseUrl = publicBaseUrl },
                cancellationToken);
            string url = string.IsNullOrWhiteSpace(result.AccessToken)
                ? string.Empty
                : $"/Delivery/{result.DeliveryId:D}?token={Uri.EscapeDataString(result.AccessToken)}";
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
                return Results.Unauthorized();

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
                return Results.Unauthorized();

            await service.MarkSyncedAsync(deliveryId, cancellationToken);
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapGet("/recipient-confirmations", async (
            HttpRequest request,
            IDeliveryService service,
            IOptions<FfpManagerBridgeOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!FfpManagerEndpointAuthorization.IsAuthorized(request, options.Value.SharedSecret))
                return Results.Unauthorized();

            IReadOnlyList<RecipientReceiptConfirmation> confirmations =
                await service.GetUnsyncedRecipientConfirmationsAsync(cancellationToken);
            return Results.Ok(confirmations);
        }).AllowAnonymous();

        group.MapPost("/{deliveryId:guid}/recipient-confirmation-synced", async (
            Guid deliveryId,
            HttpRequest request,
            IDeliveryService service,
            IOptions<FfpManagerBridgeOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!FfpManagerEndpointAuthorization.IsAuthorized(request, options.Value.SharedSecret))
                return Results.Unauthorized();

            await service.MarkRecipientConfirmationSyncedAsync(deliveryId, cancellationToken);
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapGet("/returns", async (
            HttpRequest request,
            IDeliveryService service,
            IOptions<FfpManagerBridgeOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!FfpManagerEndpointAuthorization.IsAuthorized(request, options.Value.SharedSecret))
                return Results.Unauthorized();

            IReadOnlyList<DeliveryReturnSync> returns =
                await service.GetUnsyncedReturnsAsync(cancellationToken);
            return Results.Ok(returns);
        }).AllowAnonymous();

        group.MapPost("/returns/{returnId:guid}/synced", async (
            Guid returnId,
            ReturnSyncAcknowledgement acknowledgement,
            HttpRequest request,
            IDeliveryService service,
            IOptions<FfpManagerBridgeOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!FfpManagerEndpointAuthorization.IsAuthorized(request, options.Value.SharedSecret))
                return Results.Unauthorized();

            await service.MarkReturnSyncedAsync(returnId, acknowledgement.FfpReturnId, cancellationToken);
            return Results.NoContent();
        }).AllowAnonymous();

        group.MapPost("/returns/{returnId:guid}/status", async (
            Guid returnId,
            ReturnStatusUpdate update,
            HttpRequest request,
            IDeliveryService service,
            IOptions<FfpManagerBridgeOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!FfpManagerEndpointAuthorization.IsAuthorized(request, options.Value.SharedSecret))
                return Results.Unauthorized();

            await service.ApplyReturnStatusAsync(
                returnId,
                update.Status,
                update.Detail,
                update.ChangedUtc,
                cancellationToken);
            return Results.NoContent();
        }).AllowAnonymous();

        return endpoints;
    }

    public sealed record ReturnSyncAcknowledgement(string? FfpReturnId);
    public sealed record ReturnStatusUpdate(string Status, string? Detail, DateTimeOffset ChangedUtc);
}
