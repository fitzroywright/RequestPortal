using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Portal;
using RequestPortal.Domain.Messages;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Messages;

public sealed class RequestReplyModel(
    UserManager<ApplicationUser> userManager,
    IPortalWorkflowService workflowService,
    IPortalMessageService messageService,
    RequestPortalDbContext dbContext) : PageModel
{
    [BindProperty, Required]
    public Guid RequestId { get; set; }

    [BindProperty]
    public Guid? ThreadId { get; set; }

    [BindProperty, Required, StringLength(10000)]
    public string Body { get; set; } = string.Empty;

    [BindProperty]
    public string ReturnUrl { get; set; } = "/Requests";

    public async Task<IActionResult> OnPostAsync()
    {
        ApplicationUser user = await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated user could not be resolved.");

        bool isStaff = User.HasClaim(
            StaffOidcOptions.StaffSurfaceClaim,
            StaffOidcOptions.StaffSurfaceValue);

        RequestDetail? request = await workflowService.GetRequestAsync(
            user.Id,
            RequestId,
            isStaff,
            HttpContext.RequestAborted);

        if (request is null)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Enter a reply before sending.";
            return RedirectBack();
        }

        string body = Body.Trim();

        if (ThreadId.HasValue)
        {
            MessageThreadView? thread = await messageService.GetThreadAsync(
                user.Id,
                isStaff,
                ThreadId.Value,
                HttpContext.RequestAborted);

            if (thread is null || thread.RequestId != RequestId)
            {
                return NotFound();
            }

            if (thread.ClosedUtc.HasValue)
            {
                TempData["Error"] = "This conversation is closed.";
                return RedirectBack();
            }

            await messageService.ReplyAsync(
                user.Id,
                isStaff,
                ThreadId.Value,
                body,
                HttpContext.RequestAborted);
        }
        else if (isStaff)
        {
            Guid recipientUserId = await dbContext.AssistanceRequests
                .AsNoTracking()
                .Where(item => item.Id == RequestId)
                .Select(item => item.SubmittedByUserId ?? item.CreatedByUserId)
                .SingleAsync(HttpContext.RequestAborted);

            await messageService.SendAsync(
                user.Id,
                senderIsStaff: true,
                request.OrganizationId,
                RequestId,
                threadId: null,
                $"Request # {request.PublicReference}",
                body,
                [recipientUserId],
                HttpContext.RequestAborted,
                MessageAudience.Request);
        }
        else
        {
            await messageService.SendToSharedStaffAsync(
                user.Id,
                request.OrganizationId,
                RequestId,
                threadId: null,
                $"Request # {request.PublicReference}",
                body,
                HttpContext.RequestAborted);
        }

        TempData["Success"] = "Reply sent.";
        return RedirectBack();
    }

    private IActionResult RedirectBack()
    {
        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage("/Requests/Details", new { id = RequestId });
    }
}
