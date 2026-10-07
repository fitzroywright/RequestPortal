using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Requests;

namespace RequestPortal.Web.Pages;

public sealed class QuickIntakeModel : PageModel
{
    private readonly IQuickIntakeService quickIntakeService;
    private readonly IRequestReferenceDataService referenceDataService;

    public QuickIntakeModel(
        IQuickIntakeService quickIntakeService,
        IRequestReferenceDataService referenceDataService)
    {
        this.quickIntakeService = quickIntakeService;
        this.referenceDataService = referenceDataService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<RequestCategoryOption> Categories { get; private set; } =
        Array.Empty<RequestCategoryOption>();

    public string? RequestReference { get; private set; }

    public string? WhatsAppNumber { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadReferenceDataAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await LoadReferenceDataAsync();

        if (!string.IsNullOrWhiteSpace(Input.Website))
        {
            return Page();
        }

        if (!Input.ConsentToWhatsApp)
        {
            ModelState.AddModelError(
                "Input.ConsentToWhatsApp",
                "Please confirm that FFP may send updates to this WhatsApp number.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            QuickIntakeResult result = await quickIntakeService.CreateAsync(
                new QuickIntakeSubmission(
                    Input.FirstName,
                    Input.LastName,
                    Input.MobileNumber,
                    Input.Parish,
                    Input.Town,
                    Input.Category,
                    Input.AssistanceNeeded),
                HttpContext.RequestAborted);

            RequestReference = result.PublicReference;
            WhatsAppNumber = result.MobileNumber;
            Input = new InputModel();

            return Page();
        }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(
                string.Empty,
                $"We could not create the quick request. {exception.Message}");
            return Page();
        }
    }

    private async Task LoadReferenceDataAsync()
    {
        Categories = await referenceDataService.GetCategoriesAsync(HttpContext.RequestAborted);
    }

    public sealed class InputModel
    {
        [Required, StringLength(150)]
        public string FirstName { get; set; } = string.Empty;

        [Required, StringLength(150)]
        public string LastName { get; set; } = string.Empty;

        [Required, StringLength(40)]
        [Display(Name = "WhatsApp mobile number")]
        public string MobileNumber { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string Parish { get; set; } = string.Empty;

        [StringLength(150)]
        public string? Town { get; set; }

        [StringLength(150)]
        public string? Category { get; set; }

        [Required, StringLength(4000, MinimumLength = 10)]
        [Display(Name = "What assistance do you need?")]
        public string AssistanceNeeded { get; set; } = string.Empty;

        public bool ConsentToWhatsApp { get; set; }

        public string? Website { get; set; }
    }
}
