using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Integration;

namespace RequestPortal.Web.Pages.Staff;

public sealed class IntegrationsModel : PageModel
{
    private readonly IIntegrationStatusService integrationStatusService;

    public IntegrationsModel(IIntegrationStatusService integrationStatusService)
    {
        this.integrationStatusService = integrationStatusService;
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
}
