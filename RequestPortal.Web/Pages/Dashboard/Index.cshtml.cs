using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Portal;
using RequestPortal.Application.Messages;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Dashboard;

public sealed class IndexModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly IPortalMessageService messageService;

    public IndexModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IPortalMessageService messageService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.messageService = messageService;
    }

    public string DisplayName { get; private set; } = string.Empty;

    public IReadOnlyList<OrganizationSummary> Organizations { get; private set; } = Array.Empty<OrganizationSummary>();

    public IReadOnlyList<RequestSummary> Requests { get; private set; } = Array.Empty<RequestSummary>();

    public IReadOnlyList<RequestSummary> ActionRequired { get; private set; } = Array.Empty<RequestSummary>();

    public IReadOnlyList<MailboxMessageView> RecentMessages { get; private set; } = Array.Empty<MailboxMessageView>();

    public int ApprovedOrganizations => Organizations.Count(
        organization =>
            organization.OrganizationStatus == "Approved" &&
            organization.MembershipStatus == "Approved");

    public int DraftCount => Requests.Count(request => request.Status == "Draft");

    public int SubmittedCount => Requests.Count(request => request.Status == "Submitted");

    public int ApprovedCount => Requests.Count(request => request.Status == "Approved" || request.Status == "PendingFulfilment" || request.Status == "Fulfilled" || request.Status == "Closed");

    public int MoreInformationCount => Requests.Count(request => request.Status == "MoreInformationRequired");

    public async Task OnGetAsync()
    {
        ApplicationUser user = await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");

        DisplayName = user.DisplayName;
        Organizations = await workflowService.GetOrganizationsForUserAsync(user.Id, HttpContext.RequestAborted);
        Requests = await workflowService.GetRequestsForUserAsync(user.Id, HttpContext.RequestAborted);
        ActionRequired = Requests
            .Where(request => request.Status == "MoreInformationRequired" || request.Status == "Draft")
            .OrderByDescending(request => request.CreatedUtc)
            .ToArray();

        RecentMessages = await messageService.GetInboxAsync(
            user.Id,
            isStaff: false,
            take: 3,
            cancellationToken: HttpContext.RequestAborted);
    }
}
