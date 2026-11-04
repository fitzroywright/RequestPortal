using System.Security.Claims;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Security;

namespace RequestPortal.Web.Security;

public sealed class IntegrationOperatorAuthorization(IHttpContextAccessor accessor) : IIntegrationOperatorAuthorization
{
    public Task DemandAsync(Guid actorId, CancellationToken cancellationToken = default)
    {
        var user = accessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true ||
            !user.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue) ||
            !user.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access) ||
            !user.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity) ||
            !Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) || id != actorId)
            throw new UnauthorizedAccessException("Integration recovery requires an authorized staff administrator.");
        return Task.CompletedTask;
    }
}
