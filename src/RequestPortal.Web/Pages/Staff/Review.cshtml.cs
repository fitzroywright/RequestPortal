using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Portal;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Staff;

public sealed class ReviewModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;

    public ReviewModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
    }

    public RequestDetail? Request { get; private set; }

    [BindProperty]
    [StringLength(4000)]
    public string DecisionReason { get; set; } = string.Empty;

    [BindProperty]
    [StringLength(4000)]
    public string RejectionReason { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        Request = await workflowService.GetRequestAsync(
            staffUser.Id,
            id,
            isStaff: true,
            HttpContext.RequestAborted);

        return Request is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid id)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        await workflowService.ApproveRequestAsync(staffUser.Id, id, HttpContext.RequestAborted);
        TempData["Success"] = "Request approved.";
        return RedirectToPage("/Staff/Index");
    }

    public async Task<IActionResult> OnPostMoreInformationAsync(Guid id)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();

        if (string.IsNullOrWhiteSpace(DecisionReason))
        {
            ModelState.AddModelError(nameof(DecisionReason), "Explain what additional information is required.");
            Request = await workflowService.GetRequestAsync(staffUser.Id, id, isStaff: true, HttpContext.RequestAborted);
            return Page();
        }

        await workflowService.RequestMoreInformationAsync(
            staffUser.Id,
            id,
            DecisionReason.Trim(),
            HttpContext.RequestAborted);

        TempData["Success"] = "The organization will see that more information is required.";
        return RedirectToPage("/Staff/Index");
    }

    public async Task<IActionResult> OnPostRejectAsync(Guid id)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();

        if (string.IsNullOrWhiteSpace(RejectionReason))
        {
            ModelState.AddModelError(nameof(RejectionReason), "A rejection reason is required.");
            Request = await workflowService.GetRequestAsync(staffUser.Id, id, isStaff: true, HttpContext.RequestAborted);
            return Page();
        }

        await workflowService.RejectRequestAsync(
            staffUser.Id,
            id,
            RejectionReason.Trim(),
            HttpContext.RequestAborted);

        TempData["Success"] = "Request rejected and status history updated.";
        return RedirectToPage("/Staff/Index");
    }

    private async Task<ApplicationUser> GetStaffUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated FFP staff user could not be resolved.");
    }
}
