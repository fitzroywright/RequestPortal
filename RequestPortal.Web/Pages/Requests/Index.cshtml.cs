using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Requests;

public sealed class IndexModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;

    public IndexModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
    }

    public IReadOnlyList<RequestSummary> Requests { get; private set; } = Array.Empty<RequestSummary>();

    public IReadOnlyList<string> OrganizationOptions { get; private set; } = Array.Empty<string>();

    public IReadOnlyList<string> StatusOptions { get; private set; } = Array.Empty<string>();

    public string? Query { get; private set; }

    public string? Organization { get; private set; }

    public string? Status { get; private set; }

    public async Task<IActionResult> OnGetAsync(
        string? query = null,
        string? organization = null,
        string? status = null)
    {
        if (User.HasClaim(
            RequestPortal.Web.Security.StaffOidcOptions.StaffSurfaceClaim,
            RequestPortal.Web.Security.StaffOidcOptions.StaffSurfaceValue))
        {
            return RedirectToPage("/Staff/Operations");
        }

        ApplicationUser user = await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");

        IReadOnlyList<RequestSummary> allRequests =
            await workflowService.GetRequestsForUserAsync(user.Id, HttpContext.RequestAborted);

        OrganizationOptions = allRequests
            .Select(request => request.OrganizationName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        StatusOptions = allRequests
            .Select(request => request.Status)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Query = query?.Trim();
        Organization = organization?.Trim();
        Status = status?.Trim();

        IEnumerable<RequestSummary> filtered = allRequests;

        if (!string.IsNullOrWhiteSpace(Query))
        {
            filtered = filtered.Where(request =>
                request.PublicReference.Contains(Query, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(Organization))
        {
            filtered = filtered.Where(request =>
                string.Equals(request.OrganizationName, Organization, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(Status))
        {
            filtered = filtered.Where(request =>
                string.Equals(request.Status, Status, StringComparison.OrdinalIgnoreCase));
        }

        Requests = filtered
            .OrderByDescending(request => request.SubmittedUtc ?? request.CreatedUtc)
            .ToArray();

        return Page();
    }
}
