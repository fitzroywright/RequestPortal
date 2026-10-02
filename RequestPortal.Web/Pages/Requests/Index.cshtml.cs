using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
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

    public string? Query { get; private set; }

    public async Task OnGetAsync(string? query = null)
    {
        ApplicationUser user = await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");

        Requests = await workflowService.GetRequestsForUserAsync(user.Id, HttpContext.RequestAborted);
        Query = query?.Trim();
        if (!string.IsNullOrWhiteSpace(Query))
        {
            Requests = Requests.Where(request => request.PublicReference.Contains(Query, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
    }
}
