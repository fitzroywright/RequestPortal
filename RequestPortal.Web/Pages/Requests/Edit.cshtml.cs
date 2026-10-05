using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Requests;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Requests;

public sealed class EditModel(
    UserManager<ApplicationUser> userManager,
    IRequestDraftService requestDraftService,
    IRequestReferenceDataService referenceDataService) : PageModel
{
    public string PublicReference { get; private set; } = string.Empty;
    public RequestStatus Status { get; private set; }
    public IReadOnlyList<RequestCategoryOption> Categories { get; private set; } = Array.Empty<RequestCategoryOption>();
    public IReadOnlyList<ProgramClassificationOption> Programs { get; private set; } = Array.Empty<ProgramClassificationOption>();

    [BindProperty]
    public EditInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        EditableRequestDraft? request;
        try
        {
            request = await requestDraftService.GetEditableAsync(
                user.Id,
                id,
                HttpContext.RequestAborted);
        }
        catch (InvalidOperationException exception)
        {
            TempData["Error"] = exception.Message;
            return RedirectToPage("/Requests/Details", new { id });
        }

        if (request is null)
        {
            return NotFound();
        }

        await LoadReferenceDataAsync();
        PublicReference = request.PublicReference;
        Status = request.Status;
        Input = new EditInput
        {
            Kind = request.Kind,
            Summary = request.Summary,
            Category = request.Category,
            Subcategory = request.Subcategory,
            ProgramCategory = request.ProgramCategory,
            Priority = request.Priority,
            Location = request.Location,
            Circumstances = request.Circumstances
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        EditableRequestDraft? current = await requestDraftService.GetEditableAsync(
            user.Id,
            id,
            HttpContext.RequestAborted);

        if (current is null)
        {
            return NotFound();
        }

        PublicReference = current.PublicReference;
        Status = current.Status;
        await LoadReferenceDataAsync();

        if (!string.IsNullOrWhiteSpace(Input.Category) &&
            !Categories.Any(item => string.Equals(item.Name, Input.Category, StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(Input.Category), "Select a valid request category.");
        }

        if (!string.IsNullOrWhiteSpace(Input.ProgramCategory) &&
            !Programs.Any(item => string.Equals(item.Name, Input.ProgramCategory, StringComparison.OrdinalIgnoreCase)))
        {
            ModelState.AddModelError(nameof(Input.ProgramCategory), "Select a valid program classification.");
        }

        if (Status == RequestStatus.MoreInformationRequired && Input.Kind != current.Kind)
        {
            ModelState.AddModelError(nameof(Input.Kind), "Request type cannot be changed after submission.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await requestDraftService.UpdateAsync(
                user.Id,
                id,
                Input.Kind,
                Input.Summary,
                Input.Category,
                Input.Subcategory,
                Input.ProgramCategory,
                Input.Priority,
                Input.Location,
                Input.Circumstances,
                HttpContext.RequestAborted);
        }
        catch (UnauthorizedAccessException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }

        TempData["Success"] = "Request updated.";
        return RedirectToPage("/Requests/Details", new { id });
    }

    private async Task LoadReferenceDataAsync()
    {
        Categories = await referenceDataService.GetCategoriesAsync(HttpContext.RequestAborted);
        Programs = await referenceDataService.GetProgramsAsync(HttpContext.RequestAborted);
    }

    private async Task<ApplicationUser> GetUserAsync() =>
        await userManager.GetUserAsync(User)
        ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");

    public sealed class EditInput
    {
        [Required]
        public RequestKind Kind { get; set; }

        [Required, StringLength(4000, MinimumLength = 10)]
        public string Summary { get; set; } = string.Empty;

        [StringLength(150)]
        public string? ProgramCategory { get; set; }

        [StringLength(150)]
        public string? Category { get; set; }

        [StringLength(150)]
        public string? Subcategory { get; set; }

        public RequestPriority Priority { get; set; }

        [StringLength(500)]
        public string? Location { get; set; }

        [StringLength(8000)]
        public string? Circumstances { get; set; }
    }
}
