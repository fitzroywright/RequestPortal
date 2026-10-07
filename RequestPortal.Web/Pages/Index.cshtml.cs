using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.ReferenceData;
using RequestPortal.Application.Requests;

namespace RequestPortal.Web.Pages;

public sealed class IndexModel : PageModel
{
    private readonly IQuickIntakeService quickIntakeService;
    private readonly IRequestReferenceDataService referenceDataService;

    public IndexModel(
        IQuickIntakeService quickIntakeService,
        IRequestReferenceDataService referenceDataService)
    {
        this.quickIntakeService = quickIntakeService;
        this.referenceDataService = referenceDataService;
    }

    public string FirstName { get; private set; } = "there";

    [BindProperty]
    public QuickIntakeInput QuickIntake { get; set; } = new();

    public IReadOnlyList<RequestCategoryOption> QuickIntakeCategories { get; private set; } =
        Array.Empty<RequestCategoryOption>();

    public string? QuickIntakeReference { get; private set; }

    public string? QuickIntakeMobile { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadPublicDataAsync();

        if (User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        string? value =
            User.FindFirstValue(ClaimTypes.GivenName)
            ?? User.FindFirstValue(ClaimTypes.Name);

        if (!string.IsNullOrWhiteSpace(value))
        {
            FirstName = value
                .Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }
    }

    public async Task<IActionResult> OnPostQuickIntakeAsync()
    {
        await LoadPublicDataAsync();

        // Quiet bot trap. Real users never see or fill this field.
        if (!string.IsNullOrWhiteSpace(QuickIntake.Website))
        {
            return Page();
        }

        if (!QuickIntake.ConsentToWhatsApp)
        {
            ModelState.AddModelError(
                "QuickIntake.ConsentToWhatsApp",
                "Please confirm that FFP may send request updates to this WhatsApp number.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            QuickIntakeResult result = await quickIntakeService.CreateAsync(
                new QuickIntakeSubmission(
                    QuickIntake.FirstName,
                    QuickIntake.LastName,
                    QuickIntake.MobileNumber,
                    QuickIntake.Parish,
                    QuickIntake.Town,
                    QuickIntake.Category,
                    QuickIntake.AssistanceNeeded),
                HttpContext.RequestAborted);

            QuickIntakeReference = result.PublicReference;
            QuickIntakeMobile = result.MobileNumber;
            QuickIntake = new QuickIntakeInput();

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

    private async Task LoadPublicDataAsync()
    {
        QuickIntakeCategories = await referenceDataService
            .GetCategoriesAsync(HttpContext.RequestAborted);
    }

    public sealed class QuickIntakeInput
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

        [Display(Name = "FFP may send request updates to me on WhatsApp")]
        public bool ConsentToWhatsApp { get; set; }

        public string? Website { get; set; }
    }
}
