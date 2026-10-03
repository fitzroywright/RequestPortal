using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Portal;
using RequestPortal.Web.Security;
using RequestPortal.Domain.Messages;

namespace RequestPortal.Web.Pages.Messages;

public sealed class NewModel(
    IPortalMessageService messageService,
    IPortalWorkflowService workflowService,
    IStaffOrganizationService staffOrganizationService,
    IDocumentService documentService) : PageModel
{
    public IReadOnlyList<OrganizationOption> Organizations { get; private set; } = Array.Empty<OrganizationOption>();

    public IReadOnlyList<MessageRecipientOption> Recipients { get; private set; } = Array.Empty<MessageRecipientOption>();

    public IReadOnlyList<DocumentSummary> AvailableAttachments { get; private set; } = Array.Empty<DocumentSummary>();

    public bool IsStaff { get; private set; }

    public bool CanBroadcastToOrganization { get; private set; }

    [BindProperty(SupportsGet = true)]
    public Guid? OrganizationId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? RequestId { get; set; }

    [BindProperty]
    public Guid? RecipientUserId { get; set; }

    [BindProperty]
    public bool ToFfpStaff { get; set; }

    [BindProperty]
    public string Destination { get; set; } = "ffp";

    [BindProperty]
    public MessageAudience Audience { get; set; } = MessageAudience.Individual;

    [BindProperty]
    public List<Guid> AttachmentDocumentIds { get; set; } = [];

    [BindProperty]
    [StringLength(300)]
    public string Subject { get; set; } = string.Empty;

    public string? RequestReference { get; private set; }

    public string? RequestOrganizationName { get; private set; }

    [BindProperty]
    [Required]
    [StringLength(10000)]
    public string Body { get; set; } = string.Empty;

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Guid userId = CurrentUserId();
        IsStaff = IsFfpStaff();

        if (!OrganizationId.HasValue)
        {
            ModelState.AddModelError(nameof(OrganizationId), "Choose an organization.");
        }

        if (IsStaff && Audience == MessageAudience.Individual && !RecipientUserId.HasValue)
        {
            ModelState.AddModelError(nameof(RecipientUserId), "Choose an individual recipient.");
        }

        if (IsStaff && RecipientUserId.HasValue && RecipientUserId.Value == userId)
        {
            ModelState.AddModelError(nameof(RecipientUserId), "You cannot send a message to yourself.");
        }

        if (!IsStaff &&
            string.Equals(Destination, "note", StringComparison.OrdinalIgnoreCase) &&
            !RequestId.HasValue)
        {
            ModelState.AddModelError(nameof(Destination), "Private notes must be attached to an application.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        string subject = string.IsNullOrWhiteSpace(Subject)
            ? BuildSubject()
            : Subject.Trim();

        Guid threadId;
        if (!IsStaff)
        {
            if (string.Equals(Destination, "manager", StringComparison.OrdinalIgnoreCase))
            {
                threadId = await messageService.SendAsync(
                    userId,
                    senderIsStaff: false,
                    OrganizationId,
                    RequestId,
                    threadId: null,
                    subject,
                    Body,
                    Array.Empty<Guid>(),
                    cancellationToken: HttpContext.RequestAborted,
                    audience: MessageAudience.OrganizationManager,
                    attachmentDocumentIds: AttachmentDocumentIds);
            }
            else if (string.Equals(Destination, "note", StringComparison.OrdinalIgnoreCase))
            {
                threadId = await messageService.SavePersonalNoteAsync(
                    userId,
                    OrganizationId!.Value,
                    RequestId!.Value,
                    threadId: null,
                    subject,
                    Body,
                    cancellationToken: HttpContext.RequestAborted,
                    attachmentDocumentIds: AttachmentDocumentIds);
            }
            else
            {
                threadId = await messageService.SendToSharedStaffAsync(
                    userId,
                    OrganizationId!.Value,
                    RequestId,
                    threadId: null,
                    subject,
                    Body,
                    cancellationToken: HttpContext.RequestAborted,
                    attachmentDocumentIds: AttachmentDocumentIds);
            }
        }
        else
        {
            threadId = await messageService.SendAsync(
                userId,
                senderIsStaff: true,
                OrganizationId,
                RequestId,
                threadId: null,
                subject,
                Body,
                RecipientUserId.HasValue ? [RecipientUserId.Value] : Array.Empty<Guid>(),
                cancellationToken: HttpContext.RequestAborted,
                audience: Audience,
                attachmentDocumentIds: AttachmentDocumentIds);
        }

        return RedirectToPage("/Messages/Thread", new { id = threadId });
    }

    private async Task LoadAsync()
    {
        Guid userId = CurrentUserId();
        IsStaff = IsFfpStaff();

        if (IsStaff)
        {
            IReadOnlyList<RequestPortal.Application.Administration.StaffOrganizationSummary> organizations =
                await staffOrganizationService.GetAllAsync(HttpContext.RequestAborted);
            Organizations = organizations
                .Select(item => new OrganizationOption(item.Id, item.Name))
                .ToArray();

            if (RequestId.HasValue)
            {
                RequestDetail? request = await workflowService.GetRequestAsync(
                    userId,
                    RequestId.Value,
                    isStaff: true,
                    HttpContext.RequestAborted);

                if (request is not null)
                {
                    OrganizationId = request.OrganizationId;
                    RequestReference = request.PublicReference;
                    RequestOrganizationName = request.OrganizationName;
                }
            }
        }
        else
        {
            IReadOnlyList<OrganizationSummary> organizations =
                await workflowService.GetRequestContextsForUserAsync(userId, HttpContext.RequestAborted);
            OrganizationSummary[] approvedOrganizations = organizations
                .Where(item =>
                    item.OrganizationStatus == "Approved" &&
                    item.MembershipStatus == "Approved")
                .ToArray();

            Organizations = approvedOrganizations
                .Select(item => new OrganizationOption(item.Id, item.Name))
                .ToArray();

            if (RequestId.HasValue)
            {
                IReadOnlyList<RequestSummary> requests =
                    await workflowService.GetRequestsForUserAsync(
                        userId,
                        HttpContext.RequestAborted);
                RequestSummary? request = requests.FirstOrDefault(item => item.Id == RequestId.Value);
                if (request is not null)
                {
                    OrganizationId ??= request.OrganizationId;
                    RequestReference = request.PublicReference;
                }
            }

            if (!OrganizationId.HasValue && Organizations.Count == 1)
            {
                OrganizationId = Organizations[0].Id;
            }

            if (OrganizationId.HasValue)
            {
                CanBroadcastToOrganization = approvedOrganizations.Any(item =>
                    item.Id == OrganizationId.Value &&
                    string.Equals(item.Role, "Administrator", StringComparison.OrdinalIgnoreCase));
            }
        }

        if (IsStaff)
        {
            CanBroadcastToOrganization = true;
        }

        if (OrganizationId.HasValue)
        {
            Recipients = (await messageService.GetOrganizationRecipientsAsync(
                    userId,
                    IsStaff,
                    OrganizationId.Value,
                    HttpContext.RequestAborted))
                .Where(recipient => recipient.UserId != userId)
                .ToArray();
        }

        if (RequestId.HasValue)
        {
            AvailableAttachments = await documentService.GetRequestDocumentsAsync(
                userId,
                RequestId.Value,
                IsStaff,
                HttpContext.RequestAborted);
        }
    }

    private string BuildSubject()
    {
        if (string.Equals(Destination, "note", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(RequestReference)
                ? "Application note"
                : $"Note · {RequestReference}";
        }

        return string.IsNullOrWhiteSpace(RequestReference)
            ? "Request Portal message"
            : $"Application · {RequestReference}";
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user ID is unavailable."));

    private bool IsFfpStaff() =>
        User.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);

    public sealed record OrganizationOption(Guid Id, string Name);
}
