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
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text.Json;

namespace RequestPortal.Web.Pages.Staff;

public sealed class IntegrationsModel : PageModel
{
    private readonly IIntegrationStatusService integrationStatusService;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IFileStorage storage;
    private readonly IDiagnosticOperationsRouter diagnostics;
    private readonly IStaffOperationsService staffOperationsService;
    private readonly IExternalApplicationBridgeClient bridgeClient;
    private readonly ITimeLimitedDataProtector? fieldPreviewProtector;

    public IntegrationsModel(
        IIntegrationStatusService integrationStatusService,
        UserManager<ApplicationUser> userManager,
        IFileStorage storage,
        IDiagnosticOperationsRouter diagnostics,
        IStaffOperationsService staffOperationsService,
        IExternalApplicationBridgeClient bridgeClient,
        IDataProtectionProvider? dataProtectionProvider = null)
    {
        this.integrationStatusService = integrationStatusService;
        this.userManager = userManager;
        this.storage = storage;
        this.diagnostics = diagnostics;
        this.staffOperationsService = staffOperationsService;
        this.bridgeClient = bridgeClient;
        fieldPreviewProtector = dataProtectionProvider?.CreateProtector("RequestPortal.IntegrationFields.v1").ToTimeLimitedDataProtector();
    }

    public IntegrationMonitorPage Transfers { get; private set; } = new([], 0, 1, 25, new(0,0,0,0,0));
    public IntegrationTransferDetail? Selected { get; private set; }
    public ExternalReturnOutboxStatus? ReturnOutbox { get; private set; }
    public string? ReturnOutboxError { get; private set; }
    public IReadOnlyList<IntegrationReturnObservation> ReturnHistory { get; private set; } = [];
    [BindProperty(SupportsGet = true)] public string? Source { get; set; }
    [BindProperty(SupportsGet = true)] public bool NotVerified { get; set; }
    public string ScopeLabel => (Source == "QuickIntake" ? "Quick Intakes" : Source == "RequestPortal" ? "Request Portal" : "All Sources") +
        $" — {From?.ToString("dd MMM yyyy") ?? "Beginning"}–{Through?.ToString("dd MMM yyyy") ?? "Present"} (UTC, inclusive)";
    [BindProperty(SupportsGet = true)] public string? Search { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? Through { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; } = "activity";
    [BindProperty(SupportsGet = true)] public int PageNumber { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public Guid? RequestId { get; set; }
    public bool CanOperate => User.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity);
    public ApplicationFieldPreview? FieldPreview { get; private set; }
    [BindProperty] public string? ProtectedFieldPreview { get; set; }
    [BindProperty] public string[] ApprovedFields { get; set; } = [];
    [BindProperty] public bool ConfirmFieldUpdate { get; set; }
    private sealed record FieldPreviewEnvelope(Guid Actor, ApplicationFieldPreview Preview);
    public static string StatusLabel(RequestPortal.Domain.Integration.SynchronizationStatus status) => status switch {
        RequestPortal.Domain.Integration.SynchronizationStatus.ReconciliationRequired => "Reconciliation Required",
        RequestPortal.Domain.Integration.SynchronizationStatus.NotSent => "Pending", _ => status.ToString() };
    public Dictionary<string,string> Routes(int page) => new() { ["Source"] = Source ?? "", ["NotVerified"] = NotVerified.ToString(), ["Search"] = Search ?? "", ["Status"] = Status ?? "",
        ["From"] = From?.ToString("yyyy-MM-dd") ?? "", ["Through"] = Through?.ToString("yyyy-MM-dd") ?? "", ["Sort"] = Sort ?? "activity", ["PageNumber"] = page.ToString() };
    private IntegrationMonitorQuery Query(int? page = null) => new(Search, Status,
        From.HasValue ? new DateTimeOffset(From.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
        Through.HasValue ? Through.Value == DateOnly.MaxValue ? DateTimeOffset.MaxValue : new DateTimeOffset(Through.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null,
        Sort ?? "activity", page ?? PageNumber, Source: Source, NotVerified: NotVerified);
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
    private object ReturnRoutes() => new { Source, NotVerified, Search, Status, From = From?.ToString("yyyy-MM-dd"),
        Through = Through?.ToString("yyyy-MM-dd"), Sort, PageNumber, RequestId };
    private bool ValidFilters => ModelState.IsValid &&
        (string.IsNullOrEmpty(Source) || Source is "QuickIntake" or "RequestPortal") && (!NotVerified || Source == "QuickIntake") && (Search?.Length ?? 0) <= 200 && (!From.HasValue || !Through.HasValue || From <= Through) &&
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
        catch (ExistingDestinationPreviewRequiredException exception)
        {
            return RedirectToPage("/Staff/Integrations", "FieldPreview", new { RequestId = exception.RequestId, Source, NotVerified, Search, Status,
                From = From?.ToString("yyyy-MM-dd"), Through = Through?.ToString("yyyy-MM-dd"), Sort, PageNumber });
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

    public async Task<IActionResult> OnGetFieldPreviewAsync(Guid requestId)
    {
        Response.Headers.CacheControl = "no-store";
        if (!CanOperate) return Forbid(StaffOidcOptions.StaffCookieScheme);
        if (requestId == Guid.Empty || fieldPreviewProtector is null) return BadRequest("Field preview is unavailable.");
        try
        {
            var staff = await GetStaffUserAsync();
            FieldPreview = await integrationStatusService.PreviewFieldsAsync(staff.Id, requestId, HttpContext.RequestAborted);
            ProtectedFieldPreview = fieldPreviewProtector.Protect(JsonSerializer.Serialize(new FieldPreviewEnvelope(staff.Id, FieldPreview)), TimeSpan.FromMinutes(10));
            return await OnGetAsync();
        }
        catch (UnauthorizedAccessException) { return Forbid(StaffOidcOptions.StaffCookieScheme); }
        catch (Exception exception) when (exception is InvalidOperationException or IntegrationTransferException or HttpRequestException or NotSupportedException or TimeoutException or TaskCanceledException)
        {
            TempData["Error"] = "A safe field preview could not be verified. No destination fields were changed. " + (exception is InvalidOperationException ? exception.Message : "Recheck the destination identity and availability.");
            return RedirectToPage(ReturnRoutes());
        }
    }

    public async Task<IActionResult> OnPostUpdateFieldsAsync()
    {
        Response.Headers.CacheControl = "no-store";
        if (!CanOperate) return Forbid(StaffOidcOptions.StaffCookieScheme);
        if (!ModelState.IsValid || !ConfirmFieldUpdate || fieldPreviewProtector is null || string.IsNullOrWhiteSpace(ProtectedFieldPreview) || ProtectedFieldPreview.Length > 30000)
            return BadRequest("Confirm the selected fields using a current preview.");
        try
        {
            var staff = await GetStaffUserAsync();
            var envelope = JsonSerializer.Deserialize<FieldPreviewEnvelope>(fieldPreviewProtector.Unprotect(ProtectedFieldPreview))
                ?? throw new InvalidOperationException("Preview unavailable.");
            if (envelope.Actor != staff.Id) return BadRequest("This preview belongs to a different staff session.");
            await integrationStatusService.ApplyFieldsAsync(staff.Id, envelope.Preview, ApprovedFields, HttpContext.RequestAborted);
            TempData["Success"] = "Approved FFP Manager fields updated and read back successfully.";
        }
        catch (UnauthorizedAccessException) { return Forbid(StaffOidcOptions.StaffCookieScheme); }
        catch (Exception exception) when (exception is CryptographicException or JsonException or InvalidOperationException or IntegrationTransferException or HttpRequestException or TimeoutException or TaskCanceledException)
        {
            TempData["Error"] = exception is InvalidOperationException ? exception.Message :
                "Field update was not confirmed. Do not retry automatically; generate a new preview to verify current destination values.";
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
