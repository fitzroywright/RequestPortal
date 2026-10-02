using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Identity;
using Common.Storage;
using Common.Diagnostics;

namespace RequestPortal.Web.Pages.Staff;

public sealed class IntegrationsModel : PageModel
{
    private readonly IIntegrationStatusService integrationStatusService;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IFileStorage storage;
    private readonly IDiagnosticOperationsRouter diagnostics;

    public IntegrationsModel(IIntegrationStatusService integrationStatusService, UserManager<ApplicationUser> userManager,
        IFileStorage storage, IDiagnosticOperationsRouter diagnostics)
    {
        this.integrationStatusService = integrationStatusService;
        this.userManager = userManager;
        this.storage = storage;
        this.diagnostics = diagnostics;
    }

    public IReadOnlyList<IntegrationStatusSummary> Items { get; private set; } = Array.Empty<IntegrationStatusSummary>();

    public async Task OnGetAsync()
    {
        Items = await integrationStatusService.GetAllAsync(HttpContext.RequestAborted);
    }

    public async Task<IActionResult> OnPostTestSharePointAsync()
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        StorageHealth health = await storage.CheckHealthAsync(timeout.Token);
        bool sharePoint = health.Provider.Contains("SharePoint", StringComparison.OrdinalIgnoreCase);
        bool success = sharePoint && health.Available && health.Writable;
        string detail = !sharePoint ? "The configured storage target is not SharePoint." :
            success ? "Graph authentication, site/library resolution and document read/write/delete succeeded." :
            health.Error ?? "SharePoint is unavailable or not writable.";
        await diagnostics.RouteAsync(new DiagnosticTelemetryEvent(DateTimeOffset.UtcNow,
            "RequestPortal.Common.Storage.SharePoint", "RP-SHAREPOINT-TEST",
            success ? DiagnosticSeverity.Information : DiagnosticSeverity.Error, detail,
            HttpContext.TraceIdentifier), CancellationToken.None);
        TempData[success ? "Success" : "Error"] = detail + $" Reference: {HttpContext.TraceIdentifier}";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateAsync(Guid requestId, string systemName)
    {
        if (requestId == Guid.Empty)
        {
            TempData["Error"] = "A request is required to create an integration link.";
            return RedirectToPage();
        }
        if (string.IsNullOrWhiteSpace(systemName))
        {
            TempData["Error"] = "System name is required.";
            return RedirectToPage();
        }

        try
        {
            await integrationStatusService.EnsureLinkAsync(requestId, systemName.Trim(), HttpContext.RequestAborted);
            TempData["Success"] = "Integration tracking link created.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryAsync(Guid linkId)
    {
        if (linkId == Guid.Empty)
        {
            TempData["Error"] = "An integration link is required.";
            return RedirectToPage();
        }

        try
        {
            ApplicationUser staffUser = await GetStaffUserAsync();
            await integrationStatusService.RetryAsync(staffUser.Id, linkId, HttpContext.RequestAborted);
            TempData["Success"] = "Integration submission retried.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostReconcileAsync(Guid linkId)
    {
        if (linkId == Guid.Empty)
        {
            TempData["Error"] = "An integration link is required.";
            return RedirectToPage();
        }

        try
        {
            ApplicationUser staffUser = await GetStaffUserAsync();
            await integrationStatusService.ReconcileAsync(staffUser.Id, linkId, HttpContext.RequestAborted);
            TempData["Success"] = "Integration reconciled with the external system.";
        }
        catch (InvalidOperationException exception)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage();
    }

    private async Task<ApplicationUser> GetStaffUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated FFP staff user could not be resolved.");
    }
}
