using System.ComponentModel.DataAnnotations;
using System.Globalization;
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

    [BindProperty, Required, EmailAddress, StringLength(320)] public string Email { get; set; } = string.Empty;
    [BindProperty, Required, StringLength(200, MinimumLength = 2)] public string DisplayName { get; set; } = string.Empty;
    [BindProperty, Range(1, 168)] public int ExpiresInHours { get; set; } = 24;
    [BindProperty] public string ScopeType { get; set; } = ExternalReviewerScopes.ManagementDashboard;
    [BindProperty] public Guid? OrganizationId { get; set; }
    [BindProperty] public Guid? RequestId { get; set; }
    [BindProperty] public DateTime? FromDate { get; set; }
    [BindProperty] public DateTime? ToDate { get; set; }

    public async Task OnGetAsync()
    {
        Invitations = await reviewerService.ListAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostInviteAsync()
    {
        Guid staffUserId = GetActorId();
        int hours = Math.Clamp(ExpiresInHours, 1, 168);
        string scope = BuildScope();
        if (!ModelState.IsValid)
        {
            Invitations = await reviewerService.ListAsync(HttpContext.RequestAborted);
            return Page();
        }

        string publicBaseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        await reviewerService.InviteAsync(
            staffUserId,
            Email,
            DisplayName,
            scope,
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

    public async Task<IActionResult> OnPostReissueAsync(Guid invitationId)
    {
        string publicBaseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        ExternalReviewerInvitationView invitation = await reviewerService.ReissueAsync(
            invitationId,
            GetActorId(),
            publicBaseUrl,
            HttpContext.RequestAborted);
        TempData["Success"] = $"A new 24-hour invitation was sent to {invitation.Email}.";
        return RedirectToPage();
    }

    private string BuildScope()
    {
        if (ScopeType == ExternalReviewerScopes.ManagementDashboard ||
            ScopeType == ExternalReviewerScopes.DeliverySummary)
        {
            return ScopeType;
        }

        if (ScopeType == "Organization")
        {
            if (!OrganizationId.HasValue)
            {
                ModelState.AddModelError(nameof(OrganizationId), "Organization ID is required for organization-scoped review.");
                return string.Empty;
            }
            return ExternalReviewerScopes.OrganizationPrefix + OrganizationId.Value.ToString("D");
        }

        if (ScopeType == "Request")
        {
            if (!RequestId.HasValue)
            {
                ModelState.AddModelError(nameof(RequestId), "Request ID is required for request-scoped review.");
                return string.Empty;
            }
            return ExternalReviewerScopes.RequestPrefix + RequestId.Value.ToString("D");
        }

        if (ScopeType == "DateRange")
        {
            if (!FromDate.HasValue || !ToDate.HasValue || ToDate.Value.Date < FromDate.Value.Date)
            {
                ModelState.AddModelError(nameof(FromDate), "Enter a valid from/to date range.");
                return string.Empty;
            }

            DateTimeOffset from = new(DateTime.SpecifyKind(FromDate.Value.Date, DateTimeKind.Utc));
            DateTimeOffset to = new(DateTime.SpecifyKind(ToDate.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc));
            return ExternalReviewerScopes.DateRangePrefix +
                from.ToString("O", CultureInfo.InvariantCulture) + "|" +
                to.ToString("O", CultureInfo.InvariantCulture);
        }

        ModelState.AddModelError(nameof(ScopeType), "Select a valid reviewer scope.");
        return string.Empty;
    }

    private Guid GetActorId()
    {
        string? value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out Guid id) ? id : Guid.Empty;
    }
}
