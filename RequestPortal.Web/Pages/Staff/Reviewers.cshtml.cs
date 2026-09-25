using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Reviewers;

namespace RequestPortal.Web.Pages.Staff;

public sealed class ReviewersModel : PageModel
{
    private readonly IExternalReviewerService reviewerService;

    public ReviewersModel(IExternalReviewerService reviewerService)
    {
        this.reviewerService = reviewerService;
    }

    public IReadOnlyList<ExternalReviewerInvitationView> Invitations { get; private set; } =
        Array.Empty<ExternalReviewerInvitationView>();

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string DisplayName { get; set; } = string.Empty;

    [BindProperty]
    public int ExpiresInHours { get; set; } = 24;

    public async Task OnGetAsync()
    {
        Invitations = await reviewerService.ListAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostInviteAsync()
    {
        Guid staffUserId = GetActorId();
        int hours = Math.Clamp(ExpiresInHours, 1, 168);
        string publicBaseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        await reviewerService.InviteAsync(
            staffUserId,
            Email,
            DisplayName,
            ExternalReviewerScopes.ManagementDashboard,
            DateTimeOffset.UtcNow.AddHours(hours),
            publicBaseUrl,
            HttpContext.RequestAborted);

        TempData["Success"] = $"External reviewer invitation sent to {Email}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid invitationId)
    {
        await reviewerService.RevokeAsync(invitationId, GetActorId(), HttpContext.RequestAborted);
        TempData["Success"] = "External reviewer access revoked.";
        return RedirectToPage();
    }

    private Guid GetActorId()
    {
        string? value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out Guid id) ? id : Guid.Empty;
    }
}
