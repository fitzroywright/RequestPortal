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
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; } = "activity";
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public Guid? RequestId { get; set; }
    public bool CanOperate => User.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity);
    public static string StatusLabel(RequestPortal.Domain.Integration.SynchronizationStatus status) => status switch {
        RequestPortal.Domain.Integration.SynchronizationStatus.ReconciliationRequired => "Reconciliation Required",
        RequestPortal.Domain.Integration.SynchronizationStatus.NotSent => "Pending", _ => status.ToString() };
    public Dictionary<string,string> Routes(int page) => new() { ["Search"] = Search ?? "", ["Status"] = Status ?? "",
        ["From"] = From?.ToString("yyyy-MM-dd") ?? "", ["Through"] = Through?.ToString("yyyy-MM-dd") ?? "", ["Sort"] = Sort ?? "activity", ["PageNumber"] = page.ToString() };
    private IntegrationMonitorQuery Query(int? page = null) => new(Search, Status,
        From.HasValue ? new DateTimeOffset(From.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
        Through.HasValue ? Through.Value == DateOnly.MaxValue ? DateTimeOffset.MaxValue : new DateTimeOffset(Through.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
        Sort ?? "activity", page ?? PageNumber);
    public static string StatusClass(RequestPortal.Domain.Integration.SynchronizationStatus status) => status switch {
        RequestPortal.Domain.Integration.SynchronizationStatus.Failed => "failed",
        RequestPortal.Domain.Integration.SynchronizationStatus.Retrying => "retrying",
        RequestPortal.Domain.Integration.SynchronizationStatus.Synchronized => "synchronized",
        RequestPortal.Domain.Integration.SynchronizationStatus.ReconciliationRequired => "reconciliation",
        _ => "pending"
    };
    public static string DiagnosticTitle(IntegrationTransferRow transfer) {
        if (string.Equals(transfer.ErrorCode, "LEGACY_FAILURE", StringComparison.OrdinalIgnoreCase))
            return "Original failure reason unavailable";
        string text = transfer.RecordedError ?? transfer.Explanation ?? transfer.ErrorCode ?? StatusLabel(transfer.Status);
        int observed = text.IndexOf(" Encountered ", StringComparison.Ordinal);
        if (observed >= 0) return text[(observed + 1)..];
        int sentence = text.IndexOf(". ", StringComparison.Ordinal);
        return sentence < 0 ? text : text[..(sentence + 1)];
    }
    public static string? DiagnosticDescription(IntegrationTransferRow transfer) {
        if (string.Equals(transfer.ErrorCode, "LEGACY_FAILURE", StringComparison.OrdinalIgnoreCase))
            return "No original exception was stored. Verify the destination record and review retained operational logs before retrying.";
        string? text = transfer.RecordedError ?? transfer.Explanation;
        if (text is null) return null;
        int observed = text.IndexOf(" Encountered ", StringComparison.Ordinal);
        if (observed >= 0) return text[..observed];
        int sentence = text.IndexOf(". ", StringComparison.Ordinal);
        return sentence < 0 ? null : text[(sentence + 2)..];
    }
    public static bool CanRetry(IntegrationTransferRow transfer) => transfer.LinkId.HasValue &&
        transfer.Status == RequestPortal.Domain.Integration.SynchronizationStatus.Failed;
    public static DateTimeOffset LastActivity(IntegrationTransferRow transfer) =>
        new[] { transfer.SubmittedUtc, transfer.LastAttemptUtc ?? DateTimeOffset.MinValue,
            transfer.SynchronizedUtc ?? DateTimeOffset.MinValue, transfer.VerifiedUtc ?? DateTimeOffset.MinValue }.Max();
    public Dictionary<string,string> DetailRoutes(Guid requestId) {
        var routes = Routes(Transfers.Page);
        routes["RequestId"] = requestId.ToString();
        return routes;
    }
    private object ReturnRoutes() => new { Search, Status, From = From?.ToString("yyyy-MM-dd"),
        Through = Through?.ToString("yyyy-MM-dd"), Sort, PageNumber, RequestId };
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

    // Exception frames are privileged and loaded only on explicit demand.
    // They never accompany the normal list or general staff diagnostics.
    public async Task<IActionResult> OnGetExceptionAsync(Guid requestId)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!CanOperate) return Forbid(StaffOidcOptions.StaffCookieScheme);
        if (requestId == Guid.Empty) return BadRequest();
        string? detail = await integrationStatusService.GetExceptionDetailsAsync(requestId, HttpContext.RequestAborted);
        return Content(detail ?? "No captured exception evidence is available for this request. Historical exceptions cannot be reconstructed.", "text/plain");
    }

    public async Task<IActionResult> OnGetDiagnosticsAsync(Guid requestId)
    {
        Response.Headers.CacheControl = "no-store";
        Selected = await integrationStatusService.GetRequestAsync(requestId, HttpContext.RequestAborted);
        return Selected is null ? NotFound() : Partial("_IntegrationDiagnostics", this);
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
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException ||
            exception is TaskCanceledException && !HttpContext.RequestAborted.IsCancellationRequested)
        {
            TempData["Error"] = "The integration service could not be reached. The outcome is unconfirmed; recheck the destination before retrying. Reference: " + HttpContext.TraceIdentifier;
        }

        return RedirectToPage(ReturnRoutes());
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
            TempData["Success"] = "Destination rechecked. Review the current synchronization status and recorded outcome.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or IntegrationTransferException)
        {
            TempData["Error"] = exception.Message;
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException ||
            exception is TaskCanceledException && !HttpContext.RequestAborted.IsCancellationRequested)
        {
            TempData["Error"] = "The integration service could not be reached. The outcome is unconfirmed; recheck the destination before retrying. Reference: " + HttpContext.TraceIdentifier;
        }

        return RedirectToPage(ReturnRoutes());
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
