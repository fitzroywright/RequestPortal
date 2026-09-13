using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Staff;

public sealed class IntegrationsModel : PageModel
{
    private readonly IIntegrationStatusService integrationStatusService;
    private readonly UserManager<ApplicationUser> userManager;

    public IntegrationsModel(IIntegrationStatusService integrationStatusService, UserManager<ApplicationUser> userManager)
    {
        this.integrationStatusService = integrationStatusService;
        this.userManager = userManager;
    }

    public IReadOnlyList<IntegrationStatusSummary> Items { get; private set; } = Array.Empty<IntegrationStatusSummary>();

    public async Task OnGetAsync()
    {
        Items = await integrationStatusService.GetAllAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostCreateAsync(Guid requestId, string systemName)
    {
        if (string.IsNullOrWhiteSpace(systemName))
        {
            TempData["Error"] = "System name is required.";
            return RedirectToPage();
        }
        await integrationStatusService.EnsureLinkAsync(requestId, systemName.Trim(), HttpContext.RequestAborted);
        TempData["Success"] = "Integration tracking link created.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryAsync(Guid linkId)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        await integrationStatusService.RetryAsync(staffUser.Id, linkId, HttpContext.RequestAborted);
        TempData["Success"] = "Integration submission retried.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReconcileAsync(Guid linkId)
    {
        ApplicationUser staffUser = await GetStaffUserAsync();
        await integrationStatusService.ReconcileAsync(staffUser.Id, linkId, HttpContext.RequestAborted);
        TempData["Success"] = "Integration reconciled with the external system.";
        return RedirectToPage();
    }

    private async Task<ApplicationUser> GetStaffUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated FFP staff user could not be resolved.");
    }
}
