using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Administration;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Staff;

public sealed class OperationsModel : PageModel
{
    private readonly IStaffOperationsService staffOperationsService;
    private readonly UserManager<ApplicationUser> userManager;

    public OperationsModel(IStaffOperationsService staffOperationsService, UserManager<ApplicationUser> userManager)
    {
        this.staffOperationsService = staffOperationsService;
        this.userManager = userManager;
    }

    public StaffOperationsSnapshot Snapshot { get; private set; } = new(
        Array.Empty<PendingOrganizationWorkItem>(),
        Array.Empty<RequestWorkItem>(),
        Array.Empty<RequestWorkItem>(),
        Array.Empty<DocumentWorkItem>(),
        Array.Empty<DeadLetterWorkItem>());

    public async Task OnGetAsync()
    {
        Snapshot = await staffOperationsService.GetSnapshotAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostReplayDeadLetterAsync(Guid messageId)
    {
        ApplicationUser staffUser = await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated FFP staff user could not be resolved.");
        await staffOperationsService.ReplayDeadLetterAsync(staffUser.Id, messageId, HttpContext.RequestAborted);
        TempData["Success"] = "Dead-letter message queued for safe replay.";
        return RedirectToPage();
    }
}
