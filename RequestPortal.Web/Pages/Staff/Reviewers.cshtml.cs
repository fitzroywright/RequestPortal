using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Reviewers;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Web.Pages.Staff;

public sealed record ReviewerOrganizationOption(Guid Id, string Name);
public sealed record ReviewerRequestOption(Guid Id, Guid OrganizationId, string Reference, string OrganizationName);

public sealed class ReviewersModel : PageModel
{
    private readonly IExternalReviewerService reviewerService;
    private readonly RequestPortalDbContext dbContext;

    public ReviewersModel(
        IExternalReviewerService reviewerService,
        RequestPortalDbContext dbContext)
    {
        this.reviewerService = reviewerService;
        this.dbContext = dbContext;
    }

    public IReadOnlyList<ExternalReviewerInvitationView> Invitations { get; private set; } =
        Array.Empty<ExternalReviewerInvitationView>();
    public IReadOnlyList<ReviewerOrganizationOption> Organizations { get; private set; } =
        Array.Empty<ReviewerOrganizationOption>();
    public IReadOnlyList<ReviewerRequestOption> Requests { get; private set; } =
        Array.Empty<ReviewerRequestOption>();

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string DisplayName { get; set; } = string.Empty;

    [BindProperty]
    public int ExpiresInHours { get; set; } = 24;

    [BindProperty]
    public string Scope { get; set; } = ExternalReviewerScopes.ManagementDashboard;

    [BindProperty]
    public Guid? OrganizationId { get; set; }

    [BindProperty]
    public Guid? RequestId { get; set; }

    [BindProperty]
    public DateTime? FromDate { get; set; }

    [BindProperty]
    public DateTime? ToDate { get; set; }

    public Task OnGetAsync() => LoadAsync();

    public async Task<IActionResult> OnPostInviteAsync()
    {
        Guid staffUserId = GetActorId();
        int hours = Math.Clamp(ExpiresInHours, 1, 168);
        DateTimeOffset? fromUtc = FromDate.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(FromDate.Value.Date, DateTimeKind.Utc))
            : null;
        DateTimeOffset? toUtc = ToDate.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(ToDate.Value.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc))
            : null;

        if (Scope == ExternalReviewerScopes.Organization && !OrganizationId.HasValue)
            ModelState.AddModelError(nameof(OrganizationId), "Select an organization for organization-scoped review.");
        if (Scope == ExternalReviewerScopes.Request && !RequestId.HasValue)
            ModelState.AddModelError(nameof(RequestId), "Select a request for request-scoped review.");
        if (Scope == ExternalReviewerScopes.DateRange && !fromUtc.HasValue && !toUtc.HasValue)
            ModelState.AddModelError(nameof(FromDate), "Enter a start date or end date for date-scoped review.");

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        string publicBaseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        await reviewerService.InviteScopedAsync(
            staffUserId,
            Email,
            DisplayName,
            Scope,
            DateTimeOffset.UtcNow.AddHours(hours),
            publicBaseUrl,
            OrganizationId,
            RequestId,
            fromUtc,
            toUtc,
            HttpContext.RequestAborted);

        TempData["Success"] = $"External reviewer invitation sent to {Email}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReissueAsync(Guid invitationId)
    {
        string publicBaseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}";
        await reviewerService.ReissueAsync(
            invitationId,
            GetActorId(),
            publicBaseUrl,
            DateTimeOffset.UtcNow.AddHours(24),
            HttpContext.RequestAborted);

        TempData["Success"] = "A fresh one-time reviewer invitation was sent and the previous invitation was revoked.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid invitationId)
    {
        await reviewerService.RevokeAsync(invitationId, GetActorId(), HttpContext.RequestAborted);
        TempData["Success"] = "External reviewer access revoked.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        Invitations = await reviewerService.ListAsync(HttpContext.RequestAborted);

        Organizations = await dbContext.Organizations
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => new ReviewerOrganizationOption(item.Id, item.Name))
            .ToListAsync(HttpContext.RequestAborted);

        Requests = await (
            from request in dbContext.AssistanceRequests.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on request.OrganizationId equals organization.Id
            orderby request.CreatedUtc descending
            select new ReviewerRequestOption(
                request.Id,
                request.OrganizationId,
                request.PublicReference,
                organization.Name))
            .Take(250)
            .ToListAsync(HttpContext.RequestAborted);
    }

    private Guid GetActorId()
    {
        string? value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out Guid id) ? id : Guid.Empty;
    }
}
