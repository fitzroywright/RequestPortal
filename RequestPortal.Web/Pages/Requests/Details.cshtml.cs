using System;
using System.Security.Claims;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
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
using RequestPortal.Domain.Requests;
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
    public IReadOnlyList<DeliveryReturnView> DeliveryReturns { get; private set; } = Array.Empty<DeliveryReturnView>();
    public IReadOnlyList<UnitOfMeasureOption> UnitsOfMeasure { get; private set; } = Array.Empty<UnitOfMeasureOption>();
    public IReadOnlyList<MessageThreadView> MessageThreads { get; private set; } = Array.Empty<MessageThreadView>();
    public bool DocumentsUnavailable { get; private set; }
    public bool RequestConversationReadOnly { get; private set; }
    public string? RequestConversationReadOnlyReason { get; private set; }
    public RequestDetailPageMeta DetailMeta { get; private set; } = new();
    public RequesterDetailCard RequesterCard { get; private set; } = new();
    public RelatedDetailCard RelatedCard { get; private set; } = new();

    [BindProperty] public string Reply { get; set; } = string.Empty;
    [BindProperty] public Guid? ReplyThreadId { get; set; }
    [BindProperty] public IFormFile? ReplyAttachment { get; set; }

    public async Task<IActionResult> OnPostReplyAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        RequestDetail? request = await workflowService.GetRequestAsync(user.Id, id, false, HttpContext.RequestAborted);
        if (request is null) return NotFound();

        if (IsExternalReviewer())
        {
            return Forbid();
        }

        IReadOnlyList<MessageThreadView> requestThreads =
            await messageService.GetRequestThreadsAsync(
                user.Id,
                isStaff: false,
                id,
                HttpContext.RequestAborted);

        string? messagingRestriction = GetMessagingRestriction(request, requestThreads);
        if (messagingRestriction is not null)
        {
            ModelState.AddModelError(nameof(Reply), messagingRestriction);
            return await LoadPageAsync(user, id);
        }

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

        try
        {
            if (ReplyThreadId.HasValue)
                await messageService.ReplyAsync(user.Id, false, ReplyThreadId.Value, Reply,
                    HttpContext.RequestAborted, attachments);
            else
                await messageService.SendToSharedStaffAsync(user.Id, request.OrganizationId, id, null,
                    $"Request # {request.PublicReference}", Reply, HttpContext.RequestAborted, attachments);
        }
        catch (UnauthorizedAccessException exception)
        {
            ModelState.AddModelError(nameof(Reply), exception.Message);
            return await LoadPageAsync(user, id);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(nameof(Reply), exception.Message);
            return await LoadPageAsync(user, id);
        }

        TempData["Success"] = "Message sent to FFP.";
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

    [BindProperty]
    public Guid ConfirmationDeliveryId { get; set; }

    [BindProperty]
    public List<decimal> ReceivedQuantities { get; set; } = new();

    [BindProperty]
    [StringLength(64)]
    public string? DeliveryProblemType { get; set; }

    [BindProperty]
    [StringLength(2000)]
    public string? DeliveryProblemDetail { get; set; }

    [BindProperty]
    public Guid ReturnDeliveryId { get; set; }

    [BindProperty]
    [StringLength(500)]
    public string ReturnItemDescription { get; set; } = string.Empty;

    [BindProperty]
    [Range(typeof(decimal), "0.001", "999999999999")]
    public decimal ReturnQuantity { get; set; }

    [BindProperty]
    [StringLength(2000, MinimumLength = 2)]
    public string ReturnReason { get; set; } = string.Empty;


    public async Task<IActionResult> OnPostConfirmReceiptAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        IReadOnlyList<DeliveryView> deliveries =
            await deliveryService.GetForRecipientRequestAsync(user.Id, id, HttpContext.RequestAborted);
        DeliveryView? delivery = deliveries.SingleOrDefault(item => item.Id == ConfirmationDeliveryId);
        if (delivery is null)
            return NotFound();

        DeliveryLineSnapshot[] expected = ReadDeliveryLines(delivery.SnapshotJson);
        if (ReceivedQuantities.Count != expected.Length)
        {
            ModelState.AddModelError(string.Empty, "Review every delivered item before confirming receipt.");
            return await LoadPageAsync(user, id);
        }

        List<RecipientConfirmedLine> confirmed = new();
        List<DeliveryDiscrepancy> discrepancies = new();
        for (int i = 0; i < expected.Length; i++)
        {
            decimal received = ReceivedQuantities[i];
            DeliveryLineSnapshot line = expected[i];
            if (received < 0 || received > line.Quantity)
            {
                ModelState.AddModelError(string.Empty, $"Received quantity for {line.Description} must be between 0 and {line.Quantity}.");
                return await LoadPageAsync(user, id);
            }

            confirmed.Add(new RecipientConfirmedLine(
                line.Description,
                line.Quantity,
                received,
                line.UnitOfMeasure,
                line.FfpDeliveryItemId));

            if (received != line.Quantity)
            {
                discrepancies.Add(new DeliveryDiscrepancy(
                    "IncorrectQuantity",
                    line.Description,
                    line.Quantity,
                    received,
                    null));
            }
        }

        if (!string.IsNullOrWhiteSpace(DeliveryProblemType) ||
            !string.IsNullOrWhiteSpace(DeliveryProblemDetail))
        {
            discrepancies.Add(new DeliveryDiscrepancy(
                string.IsNullOrWhiteSpace(DeliveryProblemType) ? "Other" : DeliveryProblemType.Trim(),
                null,
                null,
                null,
                DeliveryProblemDetail));
        }

        try
        {
            await deliveryService.ConfirmReceiptAsync(
                user.Id,
                id,
                delivery.Id,
                confirmed,
                discrepancies,
                HttpContext.RequestAborted);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await LoadPageAsync(user, id);
        }

        TempData["Success"] = discrepancies.Count == 0
            ? "Receipt confirmed. Food For The Poor will receive your confirmation."
            : "Receipt confirmation and delivery discrepancy recorded for Food For The Poor.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostInitiateReturnAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        if (!ModelState.IsValid)
            return await LoadPageAsync(user, id);

        try
        {
            await deliveryService.InitiateReturnAsync(
                user.Id,
                id,
                ReturnDeliveryId,
                ReturnItemDescription,
                ReturnQuantity,
                ReturnReason,
                HttpContext.RequestAborted);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await LoadPageAsync(user, id);
        }

        TempData["Success"] = "Return request sent to Food For The Poor for processing.";
        return RedirectToPage(new { id });
    }

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

        try
        {
            await workflowService.SubmitRequestAsync(user.Id, id, HttpContext.RequestAborted);
        }
        catch (RequestSubmissionBlockedException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return await LoadPageAsync(user, id);
        }

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

        var detailData = await (
            from request in dbContext.AssistanceRequests.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on request.OrganizationId equals organization.Id
            join creator in dbContext.Users.AsNoTracking()
                on request.CreatedByUserId equals creator.Id
            where request.Id == id
            select new
            {
                request.Kind,
                request.Priority,
                request.Category,
                request.Subcategory,
                request.ProgramCategory,
                request.Location,
                request.Circumstances,
                request.CreatedUtc,
                request.SubmittedUtc,
                request.SubmittedByUserId,
                OrganizationName = organization.Name,
                organization.OrganizationType,
                organization.CountryOfRegistration,
                organization.Parish,
                CreatorId = creator.Id,
                CreatorName = creator.DisplayName,
                creator.DateOfBirth,
                creator.NationalIdTrn,
                creator.PhoneNumber,
                creator.AlternativePhoneNumber,
                creator.Email,
                creator.HomeAddress,
                CreatorParish = creator.Parish,
                creator.DistrictArea,
                creator.HouseholdSize,
                creator.EmploymentStatus,
                creator.MonthlyHouseholdIncome,
                creator.HousingSituation
            })
            .SingleAsync(HttpContext.RequestAborted);

        string submittedByName = detailData.CreatorName;
        if (detailData.SubmittedByUserId.HasValue &&
            detailData.SubmittedByUserId.Value != detailData.CreatorId)
        {
            submittedByName = await dbContext.Users.AsNoTracking()
                .Where(item => item.Id == detailData.SubmittedByUserId.Value)
                .Select(item => item.DisplayName)
                .SingleOrDefaultAsync(HttpContext.RequestAborted)
                ?? detailData.CreatorName;
        }

        DateTimeOffset lastUpdated = Request.History
            .Select(item => item.ChangedUtc)
            .DefaultIfEmpty(detailData.CreatedUtc)
            .Max();

        string? ffpReference = await dbContext.IntegrationLinks.AsNoTracking()
            .Where(item => item.RequestId == id && item.SystemName == "FFPManager")
            .Select(item => item.ExternalReference)
            .SingleOrDefaultAsync(HttpContext.RequestAborted);

        bool individualRequest = string.Equals(
            detailData.OrganizationType,
            "IndividualContext",
            StringComparison.Ordinal);

        DetailMeta = new RequestDetailPageMeta(
            detailData.Kind.ToString(),
            detailData.Priority.ToString(),
            detailData.Category,
            detailData.Subcategory,
            detailData.ProgramCategory,
            detailData.Location,
            detailData.Circumstances,
            detailData.CreatedUtc,
            detailData.SubmittedUtc,
            lastUpdated,
            submittedByName,
            individualRequest);

        RequesterCard = new RequesterDetailCard(
            detailData.CreatorName,
            detailData.DateOfBirth,
            detailData.NationalIdTrn,
            detailData.PhoneNumber,
            detailData.AlternativePhoneNumber,
            detailData.Email,
            detailData.HomeAddress,
            detailData.CreatorParish,
            detailData.DistrictArea,
            detailData.HouseholdSize,
            detailData.EmploymentStatus,
            detailData.MonthlyHouseholdIncome,
            detailData.HousingSituation);

        RelatedCard = new RelatedDetailCard(
            individualRequest ? "N/A (Individual)" : detailData.OrganizationName,
            ffpReference,
            detailData.CountryOfRegistration,
            detailData.Parish);

        MessageThreads = visibleThreads;

        RequestConversationReadOnlyReason = GetMessagingRestriction(Request, visibleThreads);
        RequestConversationReadOnly = RequestConversationReadOnlyReason is not null;
        Documents = await documentService.GetRequestDocumentsAsync(
            user.Id,
            id,
            isStaff: false,
            HttpContext.RequestAborted);
        Deliveries = await deliveryService.GetForRecipientRequestAsync(
            user.Id,
            id,
            HttpContext.RequestAborted);
        DeliveryReturns = await deliveryService.GetReturnsForRequestAsync(
            user.Id,
            id,
            HttpContext.RequestAborted);
        UnitsOfMeasure = await referenceDataService.GetUnitsOfMeasureAsync(HttpContext.RequestAborted);

        if (Item.UnitOfMeasureId == Guid.Empty && UnitsOfMeasure.Count > 0)
        {
            Item.UnitOfMeasureId = UnitsOfMeasure[0].Id;
        }

        return Page();
    }

    public static DeliveryLineSnapshot[] ReadDeliveryLines(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Lines", out JsonElement lines) ||
            lines.ValueKind != JsonValueKind.Array)
            return Array.Empty<DeliveryLineSnapshot>();

        return lines.EnumerateArray()
            .Select(line => new DeliveryLineSnapshot(
                line.TryGetProperty("Description", out JsonElement description)
                    ? description.GetString() ?? string.Empty
                    : string.Empty,
                line.TryGetProperty("Quantity", out JsonElement quantity) &&
                quantity.TryGetDecimal(out decimal value)
                    ? value
                    : 0m,
                line.TryGetProperty("UnitOfMeasure", out JsonElement unit)
                    ? unit.GetString()
                    : null,
                line.TryGetProperty("FfpDeliveryItemId", out JsonElement itemId)
                    ? itemId.GetString()
                    : null))
            .ToArray();
    }

    private bool IsExternalReviewer()
    {
        return User.HasClaim("requestportal.external-review", "true");
    }

    private string? GetMessagingRestriction(
        RequestDetail request,
        IReadOnlyList<MessageThreadView> threads)
    {
        if (IsExternalReviewer())
        {
            return "External review access is read-only.";
        }

        if (request.Status is "Rejected" or "Cancelled" or "Duplicate" or "Closed")
        {
            return $"Messaging is closed because this request is {RequestStatusPresentation.Label(request.Status).ToLowerInvariant()}.";
        }

        MessageThreadView[] requestThreads = threads
            .Where(thread => thread.Audience == RequestPortal.Domain.Messages.MessageAudience.Request)
            .ToArray();

        if (requestThreads.Length > 0 && requestThreads.All(thread => thread.ClosedUtc.HasValue))
        {
            return "This request conversation is closed. Contact FFP to reopen it.";
        }

        return null;
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }

    public sealed record RequestDetailPageMeta(
        string RequestType,
        string Priority,
        string? Category,
        string? Subcategory,
        string? ProgramCategory,
        string? Location,
        string? Circumstances,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? SubmittedUtc,
        DateTimeOffset LastUpdatedUtc,
        string SubmittedByName,
        bool IsIndividualRequest)
    {
        public RequestDetailPageMeta() : this(
            string.Empty, string.Empty, null, null, null, null, null,
            default, null, default, string.Empty, false)
        {
        }
    }

    public sealed record RequesterDetailCard(
        string Name,
        DateOnly? DateOfBirth,
        string? NationalIdTrn,
        string? Phone,
        string? AlternativePhone,
        string? Email,
        string? Address,
        string? Parish,
        string? DistrictArea,
        int? HouseholdSize,
        string? EmploymentStatus,
        decimal? MonthlyHouseholdIncome,
        string? HousingSituation)
    {
        public RequesterDetailCard() : this(
            string.Empty, null, null, null, null, null, null, null, null,
            null, null, null, null)
        {
        }
    }

    public sealed record RelatedDetailCard(
        string Organization,
        string? FfpReference,
        string? Country,
        string? Parish)
    {
        public RelatedDetailCard() : this(string.Empty, null, null, null)
        {
        }
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
