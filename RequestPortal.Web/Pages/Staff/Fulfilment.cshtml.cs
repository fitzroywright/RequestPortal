using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Fulfilment;
using RequestPortal.Domain.Fulfilment;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Staff;

public sealed class FulfilmentModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IFulfilmentQueueService queueService;

    public FulfilmentModel(UserManager<ApplicationUser> userManager, IFulfilmentQueueService queueService)
    {
        this.userManager = userManager;
        this.queueService = queueService;
    }

    public IReadOnlyList<FulfilmentQueueItem> Items { get; private set; } = Array.Empty<FulfilmentQueueItem>();
    public Dictionary<Guid, IReadOnlyList<FulfilmentLineView>> LinesByRequest { get; private set; } = new();

    public async Task OnGetAsync()
    {
        Items = await queueService.GetQueueAsync(HttpContext.RequestAborted);
        foreach (FulfilmentQueueItem item in Items)
        {
            if (item.FulfilmentId.HasValue)
            {
                LinesByRequest[item.RequestId] = await queueService.GetLinesAsync(item.RequestId, HttpContext.RequestAborted);
            }
        }
    }

    public IReadOnlyList<FulfilmentLineView> GetLines(Guid requestId)
    {
        return LinesByRequest.TryGetValue(requestId, out IReadOnlyList<FulfilmentLineView>? lines)
            ? lines
            : Array.Empty<FulfilmentLineView>();
    }

    public async Task<IActionResult> OnPostStartAsync(Guid requestId, FulfilmentSource source, string? externalReference)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        await queueService.StartAsync(staffUser.Id, requestId, source.ToString(), externalReference, HttpContext.RequestAborted);
        TempData["Success"] = $"Request routed for {source} fulfilment.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostProgressAsync(Guid requestId, Guid requestItemId, decimal quantity, decimal value, string? notes)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        await queueService.RecordProgressAsync(staffUser.Id, requestId, requestItemId, quantity, value, notes, HttpContext.RequestAborted);
        TempData["Success"] = "Partial fulfilment progress recorded.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCompleteAsync(Guid requestId, string? notes)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        await queueService.CompleteAsync(staffUser.Id, requestId, notes, HttpContext.RequestAborted);
        TempData["Success"] = "Fulfilment completed and request status updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid requestId, string reason)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        await queueService.CancelAsync(staffUser.Id, requestId, reason, HttpContext.RequestAborted);
        TempData["Success"] = "Fulfilment cancelled and request returned to approved state.";
        return RedirectToPage();
    }

    private async Task<ApplicationUser> GetStaffUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated FFP staff user could not be resolved.");
    }
}
