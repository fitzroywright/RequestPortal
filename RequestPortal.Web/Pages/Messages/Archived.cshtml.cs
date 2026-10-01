using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Messages;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Messages;

public sealed class ArchivedModel(IPortalMessageService messageService) : PageModel
{
    public IReadOnlyList<MailboxMessageView> Messages { get; private set; } = Array.Empty<MailboxMessageView>();

    public bool IsStaff { get; private set; }

    public async Task OnGetAsync()
    {
        Guid userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user ID is unavailable."));
        IsStaff = User.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);
        Messages = await messageService.GetArchivedAsync(userId, IsStaff, cancellationToken: HttpContext.RequestAborted);
    }
}
