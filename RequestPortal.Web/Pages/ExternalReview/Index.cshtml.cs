using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Delivery;
using RequestPortal.Application.Management;
using RequestPortal.Application.Reviewers;

namespace RequestPortal.Web.Pages.ExternalReview;

public sealed class IndexModel : PageModel
{
    private readonly IManagementDashboardService dashboardService;
    private readonly IExternalReviewerService reviewerService;
    private readonly IDeliveryService deliveryService;

    public IndexModel(
        IManagementDashboardService dashboardService,
        IExternalReviewerService reviewerService,
        IDeliveryService deliveryService)
    {
        this.dashboardService = dashboardService;
        this.reviewerService = reviewerService;
        this.deliveryService = deliveryService;
    }

    public ManagementDashboardSnapshot? Dashboard { get; private set; }
    public IReadOnlyList<ManagementRequestRow> RestrictedRequests { get; private set; } = Array.Empty<ManagementRequestRow>();
    public string Scope { get; private set; } = string.Empty;
    public string ScopeLabel { get; private set; } = "External review";
    public bool IsRestricted { get; private set; }
    public int RestrictedAwaiting { get; private set; }
    public int RestrictedDelivered { get; private set; }
    public int RestrictedPartial { get; private set; }
    public int RestrictedFailed { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out Guid invitationId) ||
            !await reviewerService.ValidateSessionAsync(invitationId, HttpContext.RequestAborted))
        {
            await HttpContext.SignOutAsync("ExternalReviewer");
            return RedirectToPage("/ExternalReview/AccessDenied");
        }

        Scope = User.FindFirstValue("requestportal.external-review.scope") ?? string.Empty;
        if (!ExternalReviewerScopes.IsSupported(Scope))
        {
            await HttpContext.SignOutAsync("ExternalReviewer");
            return RedirectToPage("/ExternalReview/AccessDenied");
        }

        if (Scope == ExternalReviewerScopes.ManagementDashboard ||
            Scope == ExternalReviewerScopes.DeliverySummary)
        {
            Dashboard = await dashboardService.GetDashboardAsync(HttpContext.RequestAborted);
            ScopeLabel = Scope == ExternalReviewerScopes.DeliverySummary
                ? "Delivery summary"
                : "Management dashboard";
            return Page();
        }

        IsRestricted = true;
        ManagementRequestFilter filter = BuildFilter(Scope);
        RestrictedRequests = await dashboardService.GetRequestsAsync(filter, HttpContext.RequestAborted);
        ScopeLabel = BuildScopeLabel(Scope, RestrictedRequests);

        List<DeliveryView> deliveries = new();
        foreach (ManagementRequestRow request in RestrictedRequests)
        {
            deliveries.AddRange(await deliveryService.GetForRequestAsync(request.Id, HttpContext.RequestAborted));
        }

        RestrictedAwaiting = deliveries.Count(item => item.Status == "Ready");
        RestrictedDelivered = deliveries.Count(item => item.Status == "Delivered");
        RestrictedPartial = deliveries.Count(item => item.Status == "PartiallyDelivered");
        RestrictedFailed = deliveries.Count(item => item.Status == "Failed");
        return Page();
    }

    public async Task<IActionResult> OnPostSignOutAsync()
    {
        await HttpContext.SignOutAsync("ExternalReviewer");
        return RedirectToPage("/Index");
    }

    private static ManagementRequestFilter BuildFilter(string scope)
    {
        if (scope.StartsWith(ExternalReviewerScopes.OrganizationPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(scope[ExternalReviewerScopes.OrganizationPrefix.Length..], out Guid organizationId))
        {
            return new ManagementRequestFilter(OrganizationId: organizationId);
        }

        if (scope.StartsWith(ExternalReviewerScopes.RequestPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(scope[ExternalReviewerScopes.RequestPrefix.Length..], out Guid requestId))
        {
            return new ManagementRequestFilter(RequestId: requestId);
        }

        if (scope.StartsWith(ExternalReviewerScopes.DateRangePrefix, StringComparison.Ordinal))
        {
            string[] parts = scope[ExternalReviewerScopes.DateRangePrefix.Length..].Split('|', 2);
            if (parts.Length == 2 &&
                DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset from) &&
                DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset to))
            {
                return new ManagementRequestFilter(FromUtc: from, ToUtc: to);
            }
        }

        return new ManagementRequestFilter(RequestId: Guid.Empty);
    }

    private static string BuildScopeLabel(string scope, IReadOnlyList<ManagementRequestRow> requests)
    {
        if (scope.StartsWith(ExternalReviewerScopes.OrganizationPrefix, StringComparison.Ordinal))
        {
            return requests.FirstOrDefault()?.OrganizationName ?? "Selected organization";
        }

        if (scope.StartsWith(ExternalReviewerScopes.RequestPrefix, StringComparison.Ordinal))
        {
            return requests.FirstOrDefault() is ManagementRequestRow request
                ? $"Request {request.PublicReference}"
                : "Selected request";
        }

        if (scope.StartsWith(ExternalReviewerScopes.DateRangePrefix, StringComparison.Ordinal))
        {
            return "Selected date range";
        }

        return "Restricted review";
    }
}
