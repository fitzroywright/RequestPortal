using RequestPortal.Web.Security;
using System.Security.Claims;
using RequestPortal.Application.Messages;
using RequestPortal.Web.Messaging;
using RequestPortal.Web.Services;

namespace RequestPortal.Web.Startup;

internal static class EndpointRegistration
{
    public static void Map(WebApplication app)
    {
        app.MapRazorPages();

        app.MapHub<PortalMessageHub>("/hubs/messages")
            .RequireAuthorization("PortalMessages");

        MapUnreadCount(app);

        app.MapFfpManagerIntegrationEndpoints();
        app.MapDeliveryEndpoints();
        app.MapProfileAvatar();
    }

    private static void MapUnreadCount(WebApplication app)
    {
        app.MapGet(
                "/api/messages/unread-count"
                , GetUnreadCountAsync)
            .RequireAuthorization("PortalMessages");
    }

    private static async Task<IResult> GetUnreadCountAsync(
        ClaimsPrincipal user
        , IPortalMessageService messageService
        , HttpContext context)
    {
        string? userIdValue =
            user.FindFirstValue(ClaimTypes.NameIdentifier);

        if (!Guid.TryParse(userIdValue, out Guid userId))
        {
            return Results.Unauthorized();
        }

        bool isStaff = user.HasClaim(
            StaffOidcOptions.StaffSurfaceClaim
            , StaffOidcOptions.StaffSurfaceValue);

        int unread = await messageService.GetUnreadCountAsync(
            userId
            , isStaff
            , context.RequestAborted);

        return Results.Ok(new
        {
            unread
        });
    }
}
