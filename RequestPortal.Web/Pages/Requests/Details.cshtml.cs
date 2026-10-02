using System;
using System.Security.Claims;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Delivery;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Portal;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Requests;
using RequestPortal.Domain.Documents;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Web.Security;
using Common.Storage;
using Common.Diagnostics;

namespace RequestPortal.Web.Pages.Requests;

public sealed class DetailsModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly IDocumentService documentService;
    private readonly IRequestLifecycleService lifecycleService;
    private readonly IProjectProposalService projectProposalService;
    private readonly IProjectPlanningService projectPlanningService;
    private readonly IRequestReferenceDataService referenceDataService;
    private readonly IExternalApplicationIntegrationService ffpManagerIntegrationService;
    private readonly IDeliveryService deliveryService;
    private readonly RequestPortalDbContext dbContext;
    private readonly IPortalMessageService messageService;

    public DetailsModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        IDocumentService documentService,
        IRequestLifecycleService lifecycleService,
        IProjectProposalService projectProposalService,
        IProjectPlanningService projectPlanningService,
        IRequestReferenceDataService referenceDataService,
        IExternalApplicationIntegrationService ffpManagerIntegrationService,
        IDeliveryService deliveryService,
        RequestPortalDbContext dbContext,
        IPortalMessageService messageService)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.documentService = documentService;
        this.lifecycleService = lifecycleService;
        this.projectProposalService = projectProposalService;
        this.projectPlanningService = projectPlanningService;
        this.referenceDataService = referenceDataService;
        this.ffpManagerIntegrationService = ffpManagerIntegrationService;
        this.deliveryService = deliveryService;
        this.dbContext = dbContext;
        this.messageService = messageService;
    }

    public new RequestDetail? Request { get; private set; }
    public ProjectProposalDetail? ProjectProposal { get; private set; }
    public ProjectPlanningDetail? ProjectPlanning { get; private set; }
    public IReadOnlyList<DocumentSummary> Documents { get; private set; } = Array.Empty<DocumentSummary>();
    public IReadOnlyList<DeliveryView> Deliveries { get; private set; } = Array.Empty<DeliveryView>();
    public IReadOnlyList<UnitOfMeasureOption> UnitsOfMeasure { get; private set; } = Array.Empty<UnitOfMeasureOption>();
    public IReadOnlyList<MessageThreadView> MessageThreads { get; private set; } = Array.Empty<MessageThreadView>();
    public bool DocumentsUnavailable { get; private set; }
    public bool CanStartRequestConversation { get; private set; }

    [BindProperty] public string Reply { get; set; } = string.Empty;
    [BindProperty] public Guid? ReplyThreadId { get; set; }
    [BindProperty] public IFormFile? ReplyAttachment { get; set; }

    public async Task<IActionResult> OnPostReplyAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        RequestDetail? request = await workflowService.GetRequestAsync(user.Id, id, false, HttpContext.RequestAborted);
        if (request is null) return NotFound();

        if (string.IsNullOrWhiteSpace(Reply) || Reply.Length > 10000)
        {
            ModelState.AddModelError(nameof(Reply), "Enter a reply of up to 10,000 characters.");
            return await LoadPageAsync(user, id);
        }

        if (ReplyThreadId.HasValue)
        {
            MessageThreadView? thread = await messageService.GetThreadAsync(user.Id, false, ReplyThreadId.Value, HttpContext.RequestAborted);
            if (thread is null || thread.RequestId != id) return NotFound();
            if (thread.ClosedUtc.HasValue)
            {
                ModelState.AddModelError(nameof(Reply), "This conversation is closed. Contact FFP to reopen it.");
                return await LoadPageAsync(user, id);
            }
        }

        List<Guid> attachments = [];
        if (ReplyAttachment is { Length: > 0 })
        {
            try
            {
                await using System.IO.Stream content = ReplyAttachment.OpenReadStream();
                attachments.Add(await documentService.UploadForRequestAsync(user.Id, id,
                    RequestPortal.Domain.Documents.DocumentType.RequestSupport,
                    ReplyAttachment.FileName, ReplyAttachment.ContentType, ReplyAttachment.Length, content,
                    HttpContext.RequestAborted));
            }
            catch (Exception exception) when (exception is System.IO.IOException or HttpRequestException)
            {
                await ReportStorageFailureAsync(exception);
                ModelState.AddModelError(nameof(Reply), "The attachment could not be stored. Your reply has not been sent. You can retry or send it without the attachment.");
                return await LoadPageAsync(user, id);
            }
        }

        if (ReplyThreadId.HasValue)
            await messageService.ReplyAsync(user.Id, false, ReplyThreadId.Value, Reply,
                HttpContext.RequestAborted, attachments);
        else
            await messageService.SendToSharedStaffAsync(user.Id, request.OrganizationId, id, null,
                $"Request # {request.PublicReference}", Reply, HttpContext.RequestAborted, attachments);

        TempData["Success"] = "Reply sent to FFP.";
        return RedirectToPage(new { id });
    }

    [BindProperty]
    public ItemInput Item { get; set; } = new();

    [BindProperty]
    public DocumentInput Document { get; set; } = new();

    [BindProperty]
    public CancelInput Cancellation { get; set; } = new();

    [BindProperty]
    public InformationResponseInput InformationResponse { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        return await LoadPageAsync(user, id);
    }

    public async Task<IActionResult> OnPostAddItemAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        UnitsOfMeasure = await referenceDataService.GetUnitsOfMeasureAsync(HttpContext.RequestAborted);

        bool validUnit = UnitsOfMeasure.Any(unit => unit.Id == Item.UnitOfMeasureId);
        if (!validUnit)
        {
            ModelState.AddModelError(nameof(Item.UnitOfMeasureId), "Select a valid unit of measure.");
        }

        if (!ModelState.IsValid)
        {
            return await LoadPageAsync(user, id);
        }

        await workflowService.AddRequestItemAsync(
            user.Id,
            id,
            Item.ItemName,
            Item.Description,
            Item.Quantity,
            Item.UnitOfMeasureId,
            Item.Notes,
            HttpContext.RequestAborted);

        TempData["Success"] = "Requested item added.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUploadDocumentAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();

        if (Document.File is null || Document.File.Length <= 0)
        {
            ModelState.AddModelError("Document.File", "Select a file to upload.");
            return await LoadPageAsync(user, id);
        }

        try
        {
            await using System.IO.Stream content = Document.File.OpenReadStream();
            await documentService.UploadForRequestAsync(
            user.Id,
            id,
            Document.DocumentType,
            Document.File.FileName,
            Document.File.ContentType ?? "application/octet-stream",
            Document.File.Length,
            content,
                HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is System.IO.IOException or HttpRequestException)
        {
            await ReportStorageFailureAsync(exception);
            return await LoadPageAsync(user, id);
        }

        TempData["Success"] = "Supporting document uploaded.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnGetDownloadAsync(Guid id, Guid documentId)
    {
        ApplicationUser user = await GetUserAsync();
        DocumentDownload? download;
        try
        {
            download = await documentService.DownloadAsync(
            user.Id,
            documentId,
            isStaff: false,
                HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is System.IO.IOException or HttpRequestException)
        {
            await ReportStorageFailureAsync(exception);
            TempData["Error"] = "Document storage is temporarily unavailable. Please try again later.";
            return RedirectToPage(new { id });
        }

        if (download is null)
        {
            return NotFound();
        }

        return File(download.Content, download.ContentType, download.FileName);
    }

    public async Task<IActionResult> OnPostSubmitAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        await workflowService.SubmitRequestAsync(user.Id, id, HttpContext.RequestAborted);
        TempData["Success"] = "Request submitted to FFP for review.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostResubmitAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        await workflowService.ResubmitRequestAsync(user.Id, id, HttpContext.RequestAborted);
        TempData["Success"] = "Request resubmitted to FFP.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostInformationResponseAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        RequestDetail? request = await workflowService.GetRequestAsync(
            user.Id,
            id,
            isStaff: false,
            HttpContext.RequestAborted);
        if (request is null)
        {
            return NotFound();
        }

        if (!string.Equals(request.Status, "MoreInformationRequired", StringComparison.Ordinal))
        {
            TempData["Error"] = "This request is not waiting for additional information.";
            return RedirectToPage(new { id });
        }

        if (string.IsNullOrWhiteSpace(InformationResponse.Message) && InformationResponse.DocumentIds.Count == 0)
        {
            ModelState.AddModelError("InformationResponse.Message", "Enter a response or select at least one supporting document.");
            return await LoadPageAsync(user, id);
        }

        string? applicationId = await dbContext.IntegrationLinks
            .AsNoTracking()
            .Where(link => link.RequestId == id && link.SystemName == "FFPManager")
            .Select(link => link.ExternalReference)
            .SingleOrDefaultAsync(HttpContext.RequestAborted);
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            TempData["Error"] = "The FFP Manager application reference is not available yet.";
            return RedirectToPage(new { id });
        }

        await ffpManagerIntegrationService.RespondToInformationRequestAsync(
            id,
            applicationId,
            user.Id,
            InformationResponse.Message,
            InformationResponse.DocumentIds,
            HttpContext.RequestAborted);

        TempData["Success"] = "Your additional information was sent to FFP.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        if (string.IsNullOrWhiteSpace(Cancellation.Reason))
        {
            ModelState.AddModelError("Cancellation.Reason", "A cancellation reason is required.");
            return await LoadPageAsync(user, id);
        }

        await lifecycleService.CancelAsync(user.Id, id, Cancellation.Reason, HttpContext.RequestAborted);
        TempData["Success"] = "Request cancelled.";
        return RedirectToPage(new { id });
    }

    public string UnitName(Guid unitId)
    {
        UnitOfMeasureOption? unit = UnitsOfMeasure.FirstOrDefault(item => item.Id == unitId);
        return unit?.Name ?? "Unit";
    }

    private async Task ReportStorageFailureAsync(Exception exception)
    {
        DocumentsUnavailable = true;
        string correlation = HttpContext.TraceIdentifier;
        ILogger logger = HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RequestPortal.Documents");
        logger.LogError(exception, "Document operation failed. CorrelationId={CorrelationId}", correlation);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            await HttpContext.RequestServices.GetRequiredService<IDiagnosticOperationsRouter>().RouteAsync(
                new DiagnosticTelemetryEvent(DateTimeOffset.UtcNow, "RequestPortal.Common.Storage",
                    "RP-DOCUMENT-STORAGE-FAILED", DiagnosticSeverity.Error,
                    "A document operation failed. The request remains available.", correlation,
                    new Dictionary<string, string> { ["Exception"] = exception.ToString() }), timeout.Token);
        }
        catch (Exception diagnosticException)
        {
            logger.LogError(diagnosticException, "Diagnostic routing failed. CorrelationId={CorrelationId}", correlation);
        }
    }

    private async Task<IActionResult> LoadPageAsync(ApplicationUser user, Guid id)
    {
        Request = await workflowService.GetRequestAsync(
            user.Id,
            id,
            isStaff: false,
            HttpContext.RequestAborted);
        if (Request is null)
        {
            return NotFound();
        }

        ProjectProposal = await projectProposalService.GetAsync(
            user.Id,
            id,
            isStaff: false,
            HttpContext.RequestAborted);
        if (ProjectProposal is not null)
        {
            ProjectPlanning = await projectPlanningService.GetAsync(
                user.Id,
                id,
                isStaff: false,
                HttpContext.RequestAborted);
        }

        Guid userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user ID is unavailable."));
        bool isStaff = User.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);
        Guid[] threadIds = await dbContext.MessageThreads
            .AsNoTracking()
            .Where(thread => thread.RequestId == id)
            .OrderByDescending(thread => thread.Id == id)
            .ThenByDescending(thread => thread.CreatedUtc)
            .Select(thread => thread.Id)
            .Take(20)
            .ToArrayAsync(HttpContext.RequestAborted);

        List<MessageThreadView> visibleThreads = [];
        foreach (Guid threadId in threadIds)
        {
            MessageThreadView? thread = await messageService.GetThreadAsync(
                userId,
                isStaff,
                threadId,
                HttpContext.RequestAborted);
            if (thread is not null)
            {
                visibleThreads.Add(thread);
            }
        }

        MessageThreads = visibleThreads;
        CanStartRequestConversation = await dbContext.AssistanceRequests.AsNoTracking()
            .AnyAsync(item => item.Id == id && (item.CreatedByUserId == user.Id || item.SubmittedByUserId == user.Id), HttpContext.RequestAborted)
            || visibleThreads.Any(item => item.Audience == RequestPortal.Domain.Messages.MessageAudience.Request)
            || await dbContext.OrganizationMemberships.AsNoTracking().AnyAsync(item =>
                item.OrganizationId == Request.OrganizationId && item.UserId == user.Id &&
                item.Status == RequestPortal.Domain.Organizations.MembershipStatus.Approved &&
                item.Role == RequestPortal.Domain.Organizations.OrganizationRole.Administrator && item.ReceiveRequestNotifications,
                HttpContext.RequestAborted);
        Documents = await documentService.GetRequestDocumentsAsync(
            user.Id,
            id,
            isStaff: false,
            HttpContext.RequestAborted);
        Deliveries = await deliveryService.GetForRequestAsync(id, HttpContext.RequestAborted);
        UnitsOfMeasure = await referenceDataService.GetUnitsOfMeasureAsync(HttpContext.RequestAborted);

        if (Item.UnitOfMeasureId == Guid.Empty && UnitsOfMeasure.Count > 0)
        {
            Item.UnitOfMeasureId = UnitsOfMeasure[0].Id;
        }

        return Page();
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }

    public sealed class ItemInput
    {
        [Required]
        [StringLength(250, MinimumLength = 1)]
        public string ItemName { get; set; } = string.Empty;

        [Required]
        [StringLength(2000, MinimumLength = 2)]
        public string Description { get; set; } = string.Empty;

        [Range(typeof(decimal), "0.001", "999999999999")]
        public decimal Quantity { get; set; } = 1;

        [Required]
        public Guid UnitOfMeasureId { get; set; }

        [StringLength(2000)]
        public string? Notes { get; set; }
    }

    public sealed class DocumentInput
    {
        public DocumentType DocumentType { get; set; } = DocumentType.RequestSupport;
        public IFormFile? File { get; set; }
    }

    public sealed class CancelInput
    {
        [StringLength(1000)]
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class InformationResponseInput
    {
        [StringLength(4000)]
        public string Message { get; set; } = string.Empty;

        public List<Guid> DocumentIds { get; set; } = new();
    }
}
