using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Portal;
using RequestPortal.Web.Security;

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

    [BindProperty(SupportsGet = true)]
    public Guid? OrganizationId { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? RequestId { get; set; }

    [BindProperty]
    public Guid? RecipientUserId { get; set; }

    [BindProperty]
    public bool ToFfpStaff { get; set; }

    [BindProperty]
    public List<Guid> AttachmentDocumentIds { get; set; } = [];

    [BindProperty]
    [Required]
    [StringLength(300)]
    public string Subject { get; set; } = string.Empty;

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

        if (IsStaff)
        {
            if (!RecipientUserId.HasValue)
            {
                ModelState.AddModelError(nameof(RecipientUserId), "Choose a representative.");
            }
        }
        else if (!ToFfpStaff && !RecipientUserId.HasValue)
        {
            ModelState.AddModelError(nameof(RecipientUserId), "Choose FFP Staff or an organization representative.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        Guid threadId;
        if (!IsStaff && ToFfpStaff)
        {
            threadId = await messageService.SendToSharedStaffAsync(
                userId,
                OrganizationId!.Value,
                RequestId,
                null,
                Subject,
                Body,
                cancellationToken: HttpContext.RequestAborted,
                attachmentDocumentIds: AttachmentDocumentIds);
        }
        else
        {
            threadId = await messageService.SendAsync(
                userId,
                IsStaff,
                OrganizationId,
                RequestId,
                null,
                Subject,
                Body,
                [RecipientUserId!.Value],
                cancellationToken: HttpContext.RequestAborted,
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
            IReadOnlyList<StaffOrganizationSummary> organizations =
                await staffOrganizationService.GetAllAsync(HttpContext.RequestAborted);
            Organizations = organizations
                .Select(item => new OrganizationOption(item.Id, item.Name))
                .ToArray();
        }
        else
        {
            IReadOnlyList<OrganizationSummary> organizations =
                await workflowService.GetOrganizationsForUserAsync(userId, HttpContext.RequestAborted);
            Organizations = organizations
                .Where(item =>
                    item.OrganizationStatus == "Approved" &&
                    item.MembershipStatus == "Approved")
                .Select(item => new OrganizationOption(item.Id, item.Name))
                .ToArray();
        }

        if (OrganizationId.HasValue)
        {
            Recipients = await messageService.GetOrganizationRecipientsAsync(
                userId,
                IsStaff,
                OrganizationId.Value,
                HttpContext.RequestAborted);
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

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user ID is unavailable."));

    private bool IsFfpStaff() =>
        User.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);

    public sealed record OrganizationOption(Guid Id, string Name);
}
