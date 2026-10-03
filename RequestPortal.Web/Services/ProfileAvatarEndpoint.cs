using Common.Storage;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Services;

internal static class ProfileAvatarEndpoint
{
    public static RouteHandlerBuilder MapProfileAvatar(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet(
                "/api/users/{userId:guid}/avatar",
                GetAvatarAsync)
            .RequireAuthorization("PortalMessages");
    }

    private static async Task<IResult> GetAvatarAsync(
        Guid userId,
        RequestPortalDbContext dbContext,
        IFileStorage fileStorage,
        HttpContext context)
    {
        var photo = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                user.ProfilePhotoStorageKey,
                user.ProfilePhotoContentType
            })
            .SingleOrDefaultAsync(context.RequestAborted);

        if (photo is null ||
            string.IsNullOrWhiteSpace(photo.ProfilePhotoStorageKey))
        {
            return Results.Redirect("/img/generic-avatar.svg");
        }

        try
        {
            Stream stream = await fileStorage.OpenReadAsync(
                photo.ProfilePhotoStorageKey,
                context.RequestAborted);

            return Results.Stream(
                stream,
                photo.ProfilePhotoContentType ?? "image/jpeg");
        }
        catch (FileNotFoundException)
        {
            return Results.Redirect("/img/generic-avatar.svg");
        }
    }
}
