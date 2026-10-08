using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;\nusing RequestPortal.Application.Documents;\nusing RequestPortal.Domain.Documents;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Requests;

namespace RequestPortal.Web.Pages;

public sealed class QuickIntakeModel : PageModel
{
    private readonly IQuickIntakeService quickIntakeService;\n    private readonly IDocumentService documentService;

    public QuickIntakeModel(IQuickIntakeService quickIntakeService, IDocumentService documentService)
    {
        this.quickIntakeService = quickIntakeService;\n        this.documentService = documentService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty] public List<IFormFile> Attachments { get; set; } = new();\n    [BindProperty] public List<IFormFile> CameraPhotos { get; set; } = new();\n    public string? AttachmentWarning { get; private set; }\n    public string? RequestReference { get; private set; }
    public string? Telephone { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!string.IsNullOrWhiteSpace(Input.Website))
            return Page();

        if (Input.DateOfBirth is null)
            ModelState.AddModelError("Input.DateOfBirth", "Date of birth is required.");

        if (Input.AssistancePrograms.Count == 0)
            ModelState.AddModelError("Input.AssistancePrograms", "Select at least one programme or None.");

        if (Input.AssistancePrograms.Contains("None", StringComparer.OrdinalIgnoreCase) &&
            Input.AssistancePrograms.Count > 1)
            ModelState.AddModelError("Input.AssistancePrograms", "None cannot be selected with another programme.");

        var files = Attachments.Concat(CameraPhotos).Where(file => file.Length > 0).ToArray();\n        if (files.Length > 5 || files.Any(file => file.Length > 10 * 1024 * 1024))\n            ModelState.AddModelError(nameof(Attachments), "Attach up to 5 files, each no larger than 10 MB.");\n\n        if (!ModelState.IsValid)
            return Page();

        try
        {
            QuickIntakeResult result = await quickIntakeService.CreateAsync(
                new QuickIntakeSubmission(
                    Input.FirstName,
                    Input.LastName,
                    Input.DateOfBirth!.Value,
                    Input.Gender,
                    Input.MaritalStatus,
                    Input.Telephone,
                    Input.Email,
                    Input.StreetAddress,
                    Input.CommunityDistrict,
                    Input.Parish,
                    Input.EmploymentStatus,
                    Input.MonthlyIncome,
                    string.Equals(Input.HasChildrenUnder18LivingFullTime, "Yes", StringComparison.OrdinalIgnoreCase),
                    string.Equals(Input.HasDisabilityOrSeriousMedicalCondition, "Yes", StringComparison.OrdinalIgnoreCase),
                    Input.AssistancePrograms,
                    Input.HelpCategory),
                HttpContext.RequestAborted);

            RequestReference = result.PublicReference;
            if (files.Length > 0)
            {
                try
                {
                    Guid uploaderId = await quickIntakeService.GetAnonymousUploaderIdAsync(result.RequestId, HttpContext.RequestAborted);
                    foreach (IFormFile file in files)
                    {
                        await using var stream = file.OpenReadStream();
                        await documentService.UploadForRequestAsync(uploaderId, result.RequestId,
                            DocumentType.RequestSupport, file.FileName, file.ContentType, file.Length,
                            stream, HttpContext.RequestAborted);
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException)
                {
                    AttachmentWarning = "Your request was received, but one or more attachments could not be saved. Please quote your reference when contacting us.";
                }
            }
            Telephone = result.Telephone;
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
            ModelState.AddModelError(string.Empty, $"We could not create the quick request. {exception.Message}");
            return Page();
        }
    }

    public sealed class InputModel
    {
        [Required, StringLength(150)]
        public string FirstName { get; set; } = string.Empty;

        [Required, StringLength(150)]
        public string LastName { get; set; } = string.Empty;

        public DateOnly? DateOfBirth { get; set; }

        [Required, RegularExpression("Male|Female|Non-Binary")]
        public string Gender { get; set; } = string.Empty;

        [Required, RegularExpression("Single|Married|Divorced|Widowed")]
        public string MaritalStatus { get; set; } = string.Empty;

        [Required, StringLength(40)]
        public string Telephone { get; set; } = string.Empty;

        [EmailAddress, StringLength(320)]
        public string? Email { get; set; }

        [Required, StringLength(250)]
        public string StreetAddress { get; set; } = string.Empty;

        [Required, StringLength(150)]
        public string CommunityDistrict { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string Parish { get; set; } = string.Empty;

        [Required, RegularExpression("Employed|Self Employed|Unemployed|Student")]
        public string EmploymentStatus { get; set; } = string.Empty;

        [Range(typeof(decimal), "0", "999999999999.99")]
        public decimal MonthlyIncome { get; set; }

        [Required, RegularExpression("Yes|No")]
        public string HasChildrenUnder18LivingFullTime { get; set; } = string.Empty;

        [Required, RegularExpression("Yes|No")]
        public string HasDisabilityOrSeriousMedicalCondition { get; set; } = string.Empty;

        public List<string> AssistancePrograms { get; set; } = new();

        [Required, RegularExpression("Food|Housing|Earn an Income|Education|Health|Water & Hygiene")]
        public string HelpCategory { get; set; } = string.Empty;

        public string? Website { get; set; }
    }
}
