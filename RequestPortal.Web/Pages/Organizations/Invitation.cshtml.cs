using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Organizations;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Organizations;

public sealed class InvitationModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IOrganizationInvitationService invitationService;

    public InvitationModel(
        UserManager<ApplicationUser> userManager,
        IOrganizationInvitationService invitationService)
    {
        this.userManager = userManager;
        this.invitationService = invitationService;
    }

    public OrganizationInvitationView? Invitation { get; private set; }

    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty(SupportsGet = true)]
    public string Token { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync()
    {
        ApplicationUser user = await GetUserAsync();
        IReadOnlyList<OrganizationInvitationView> pending =
            await invitationService.GetPendingForUserAsync(
                user.Id,
                user.Email,
                HttpContext.RequestAborted);

        Invitation = pending.FirstOrDefault(item => item.Id == Id);
        if (Invitation is null)
        {
            TempData["Error"] = "This invitation is unavailable, expired, or belongs to a different email address.";
            return RedirectToPage("/Organizations/Index");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAcceptAsync()
    {
        ApplicationUser user = await GetUserAsync();

        try
        {
            await invitationService.AcceptAsync(
                user.Id,
                user.Email,
                Id,
                Token,
                HttpContext.RequestAborted);

            TempData["Success"] = "Organization invitation accepted.";
            return RedirectToPage("/Organizations/Index");
        }
        catch (InvalidOperationException exception)
        {
            TempData["Error"] = exception.Message;
            return RedirectToPage("/Organizations/Index");
        }
        catch (ArgumentException exception)
        {
            TempData["Error"] = exception.Message;
            return RedirectToPage("/Organizations/Index");
        }
    }

    private async Task<ApplicationUser> GetUserAsync() =>
        await userManager.GetUserAsync(User)
        ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
}
