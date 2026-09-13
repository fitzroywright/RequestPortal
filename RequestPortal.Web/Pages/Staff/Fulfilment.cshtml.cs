using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Fulfilment;
using RequestPortal.Application.Requests;
using RequestPortal.Domain.Fulfilment;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Staff;

public sealed class FulfilmentModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IFulfilmentQueueService queueService;
    private readonly IRequestLifecycleService lifecycleService;

    public FulfilmentModel(
        UserManager<ApplicationUser> userManager,
        IFulfilmentQueueService queueService,
        IRequestLifecycleService lifecycleService)
    {
        this.userManager = userManager;
        this.queueService = queueService;
        this.lifecycleService = lifecycleService;
    }

    public IReadOnlyList<FulfilmentQueueItem> Items { get; private set; } = Array.Empty<FulfilmentQueueItem>();

    public async Task OnGetAsync()
    {
        Items = await queueService.GetQueueAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostStartAsync(Guid requestId, FulfilmentSource source)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        await lifecycleService.StartFulfilmentAsync(
            staffUser.Id,
            requestId,
            source,
            HttpContext.RequestAborted);

        TempData["Success"] = $"Request routed for {source} fulfilment.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCompleteAsync(Guid fulfilmentId)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        await lifecycleService.CompleteFulfilmentAsync(
            staffUser.Id,
            fulfilmentId,
            null,
            HttpContext.RequestAborted);

        TempData["Success"] = "Fulfilment completed and request status updated.";
        return RedirectToPage();
    }

    private async Task<ApplicationUser> GetStaffUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated FFP staff user could not be resolved.");
    }
}
