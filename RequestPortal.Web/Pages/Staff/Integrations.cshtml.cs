using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Integration;
using RequestPortal.Infrastructure.Identity;
using Common.Storage;
using Common.Diagnostics;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Staff;

public sealed class IntegrationsModel : PageModel
{
    private readonly IIntegrationStatusService integrationStatusService;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IFileStorage storage;
    private readonly IDiagnosticOperationsRouter diagnostics;
    private readonly IStaffOperationsService staffOperationsService;
    private readonly IExternalApplicationBridgeClient bridgeClient;

    public IntegrationsModel(
        IIntegrationStatusService integrationStatusService,
        UserManager<ApplicationUser> userManager,
        IFileStorage storage,
        IDiagnosticOperationsRouter diagnostics,
        IStaffOperationsService staffOperationsService,
        IExternalApplicationBridgeClient bridgeClient)
    {
        this.integrationStatusService = integrationStatusService;
        this.userManager = userManager;
        this.storage = storage;
        this.diagnostics = diagnostics;
        this.staffOperationsService = staffOperationsService;
        this.bridgeClient = bridgeClient;
    }

    public IntegrationMonitorPage Transfers { get; private set; } = new([], 0, 1, 25, new(0,0,0,0,0));
    public IntegrationTransferDetail? Selected { get; private set; }
    public ExternalReturnOutboxStatus? ReturnOutbox { get; private set; }
    public string? ReturnOutboxError { get; private set; }
    public IReadOnlyList<IntegrationReturnObservation> ReturnHistory { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Through { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; } = "received";
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public Guid? RequestId { get; set; }
    public bool CanOperate => User.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity);
    public static string StatusLabel(RequestPortal.Domain.Integration.SynchronizationStatus status) => status switch {
        RequestPortal.Domain.Integration.SynchronizationStatus.ReconciliationRequired => "Reconciliation Required",
        RequestPortal.Domain.Integration.SynchronizationStatus.NotSent => "Pending", _ => status.ToString() };
    public Dictionary<string,string> Routes(int page) => new() { ["Search"] = Search ?? "", ["Status"] = Status ?? "",
        ["From"] = From?.ToString("yyyy-MM-dd") ?? "", ["Through"] = Through?.ToString("yyyy-MM-dd") ?? "", ["Sort"] = Sort ?? "received", ["PageNumber"] = page.ToString() };
    private IntegrationMonitorQuery Query(int? page = null) => new(Search, Status,
        From.HasValue ? new DateTimeOffset(From.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
        Through.HasValue ? Through.Value == DateOnly.MaxValue ? DateTimeOffset.MaxValue : new DateTimeOffset(Through.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
        Sort ?? "received", page ?? PageNumber);
    private bool ValidFilters => ModelState.IsValid && (Search?.Length ?? 0) <= 200 && (!From.HasValue || !Through.HasValue || From <= Through) &&
        (string.IsNullOrWhiteSpace(Status) || Enum.TryParse<RequestPortal.Domain.Integration.SynchronizationStatus>(Status,true,out var status) && Enum.IsDefined(status));
    public IReadOnlyList<DeadLetterWorkItem> DeadLetters { get; private set; } = Array.Empty<DeadLetterWorkItem>();
    public IReadOnlyList<NotificationDeliveryWorkItem> FailedNotificationDeliveries { get; private set; } = Array.Empty<NotificationDeliveryWorkItem>();

    public async Task<IActionResult> OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        if(!ValidFilters) return BadRequest("Select valid integration filters and a valid date range.");
        Transfers = await integrationStatusService.SearchAsync(Query(), HttpContext.RequestAborted);
        if (RequestId.HasValue)
        {
            Selected = await integrationStatusService.GetRequestAsync(RequestId.Value, HttpContext.RequestAborted);
            if (Selected is null) return NotFound();
        }
        StaffOperationsSnapshot snapshot = await staffOperationsService.GetSnapshotAsync(HttpContext.RequestAborted);
        DeadLetters = snapshot.DeadLetters;
        FailedNotificationDeliveries = snapshot.FailedNotificationDeliveries;
        ReturnHistory = await integrationStatusService.GetReturnHistoryAsync(HttpContext.RequestAborted);
        try { ReturnOutbox = await bridgeClient.GetReturnOutboxAsync(HttpContext.RequestAborted); }
        catch (Exception exception) when (exception is HttpRequestException or IntegrationTransferException or NotSupportedException or TaskCanceledException)
        { ReturnOutboxError = "Return-event diagnostics could not be read. Initial submission status is shown separately; inspect bridge readiness and callback delivery."; }
        return Page();
    }

    public async Task<IActionResult> OnPostTestSharePointAsync()
    {
        if (!CanOperate) return Forbid(StaffOidcOptions.StaffCookieScheme);
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

    public async Task<IActionResult> OnPostRetryAsync(Guid linkId)
    {
        if (!CanOperate) return Forbid(StaffOidcOptions.StaffCookieScheme);
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
        catch (Exception exception) when (exception is InvalidOperationException or IntegrationTransferException)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage(new { RequestId });
    }

    public async Task<IActionResult> OnPostReconcileAsync(Guid linkId)
    {
        if (!CanOperate) return Forbid(StaffOidcOptions.StaffCookieScheme);
        if (linkId == Guid.Empty && !RequestId.HasValue)
        {
            TempData["Error"] = "An integration link is required.";
            return RedirectToPage();
        }

        try
        {
            ApplicationUser staffUser = await GetStaffUserAsync();
            if (RequestId.HasValue)
                await integrationStatusService.ReconcileRequestAsync(staffUser.Id, RequestId.Value, HttpContext.RequestAborted);
            else await integrationStatusService.ReconcileAsync(staffUser.Id, linkId, HttpContext.RequestAborted);
            TempData["Success"] = "Integration reconciled with the external system.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or IntegrationTransferException)
        {
            TempData["Error"] = exception.Message;
        }

        return RedirectToPage(new { RequestId });
    }

    public async Task<IActionResult> OnPostReplayDeadLetterAsync(Guid messageId)
    {
        if (!CanOperate) return Forbid(StaffOidcOptions.StaffCookieScheme);
        try
        {
            ApplicationUser staffUser = await GetStaffUserAsync();
            await staffOperationsService.ReplayDeadLetterAsync(
                staffUser.Id,
                messageId,
                HttpContext.RequestAborted);
            TempData["Success"] = "Dead-letter message queued for safe replay.";
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
