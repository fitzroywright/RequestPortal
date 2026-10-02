using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using RequestPortal.Application.Messages;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.ViewComponents;

public sealed class RequestConversationViewComponent(
    IPortalMessageService messageService) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        Guid requestId,
        string returnUrl,
        bool compact = false)
    {
        string? rawUserId = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(rawUserId, out Guid userId))
        {
            return Content(string.Empty);
        }

        bool isStaff = UserClaimsPrincipal.HasClaim(
            StaffOidcOptions.StaffSurfaceClaim,
            StaffOidcOptions.StaffSurfaceValue);

        IReadOnlyList<MessageThreadView> threads =
            await messageService.GetRequestThreadsAsync(
                userId,
                isStaff,
                requestId,
                HttpContext.RequestAborted);

        return View(new RequestConversationPanelModel(
            requestId,
            returnUrl,
            isStaff,
            compact,
            threads));
    }
}

public sealed record RequestConversationPanelModel(
    Guid RequestId,
    string ReturnUrl,
    bool IsStaff,
    bool Compact,
    IReadOnlyList<MessageThreadView> Threads);
