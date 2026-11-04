using Microsoft.AspNetCore.Mvc;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.ViewComponents;

public sealed class IntegrationStatusViewComponent(IIntegrationStatusService integrations) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(Guid? requestId = null)
    {
        if (!UserClaimsPrincipal.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue) ||
            !UserClaimsPrincipal.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access)) return Content("");
        if (requestId.HasValue)
        {
            var row = await integrations.GetTransferAsync(requestId.Value, HttpContext.RequestAborted);
            return row is null ? Content("") : View("Default", row);
        }
        return View("Attention", await integrations.CountAttentionAsync(HttpContext.RequestAborted));
    }
}
