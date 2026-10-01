using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Messages;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Messages;

public sealed class AssignedModel(IPortalMessageService messageService) : PageModel
{
    public IReadOnlyList<MailboxMessageView> Messages { get; private set; } = Array.Empty<MailboxMessageView>();

    public async Task OnGetAsync()
    {
        Guid userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user ID is unavailable."));

        if (!User.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue))
        {
            throw new UnauthorizedAccessException("FFP staff access is required.");
        }

        Messages = await messageService.GetAssignedToMeAsync(
            userId,
            cancellationToken: HttpContext.RequestAborted);
    }
}
