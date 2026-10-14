using Microsoft.AspNetCore.Identity;
using RequestPortal.Infrastructure.Identity;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RequestPortal.Application.Documents;
using RequestPortal.Domain.Documents;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Requests;

namespace RequestPortal.Web.Pages;

public sealed class QuickIntakeModel : PageModel
{
    private readonly IQuickIntakeService quickIntakeService;
    private readonly IDocumentService documentService;
    private readonly UserManager<ApplicationUser> userManager;

    public QuickIntakeModel(IQuickIntakeService quickIntakeService, IDocumentService documentService, UserManager<ApplicationUser> userManager)
    {
        this.quickIntakeService = quickIntakeService;
        this.documentService = documentService;
        this.userManager = userManager;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty] public Guid SubmissionId { get; set; }
    [BindProperty] public Guid RequestId { get; set; }
    [BindProperty] public int AttachmentIndex { get; set; }
    [BindProperty] public IFormFile? AttachmentFile { get; set; }

    [BindProperty] public List<IFormFile> Attachments { get; set; } = new();
    [BindProperty] public List<IFormFile> CameraPhotos { get; set; } = new();
    public string? AttachmentWarning { get; private set; }
    public string? RequestReference { get; private set; }
    public string? Telephone { get; private set; }

    public async Task OnGetAsync()
    {
        ApplicationUser? user = User.Identity?.IsAuthenticated == true
            ? await userManager.GetUserAsync(User) : null;
        if (user is null) return;
        string[] name = (user.DisplayName ?? "").Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        Input.FirstName = name.FirstOrDefault() ?? "";
        Input.LastName = name.Length > 1 ? name[1] : "";
        Input.DateOfBirth = user.DateOfBirth;
        Input.Gender = string.Equals(user.Gender, "Non-Binary", StringComparison.OrdinalIgnoreCase)
            ? "Prefer not to say" : user.Gender ?? "";
        Input.MaritalStatus = user.MaritalStatus ?? "";
        Input.Telephone = user.PhoneNumber ?? "";
        Input.Email = user.Email;
        Input.StreetAddress = user.HomeAddress ?? "";
        Input.CommunityDistrict = user.DistrictArea ?? user.Town ?? "";
        Input.Parish = user.Parish ?? "";
        Input.EmploymentStatus = user.EmploymentStatus ?? "";
        Input.MonthlyIncome = user.MonthlyHouseholdIncome ?? 0;
    }

    private async Task<Guid?> GetApplicantIdAsync()
    {
        if (User.Identity?.IsAuthenticated != true) return null;
        ApplicationUser user = await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("The signed-in applicant could not be resolved.");
        return user.Id;
    }

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

        var files = Attachments.Concat(CameraPhotos).Where(file => file.Length > 0).ToArray();
        if (files.Length > 5 || files.Any(file => file.Length > 10 * 1024 * 1024))
            ModelState.AddModelError(nameof(Attachments), "Attach up to 5 files, each no larger than 10 MB.");

        try { QuickIntakeVictimCircumstances.Normalize(Input.VictimCircumstances); }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError("Input.VictimCircumstances", exception.Message);
        }
        if (!ModelState.IsValid)
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
                    Input.HelpCategory, Input.SecondaryTelephone, Input.VictimCircumstances),
                HttpContext.RequestAborted, applicantUserId: await GetApplicantIdAsync());

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

    public async Task<IActionResult> OnPostStartAsync()
    {
        if (SubmissionId == Guid.Empty || !string.IsNullOrWhiteSpace(Input.Website))
            return BadRequest(new { error = "Invalid submission." });
        if (Input.DateOfBirth is null)
            ModelState.AddModelError("Input.DateOfBirth", "Date of birth is required.");
        if (Input.AssistancePrograms.Count == 0 ||
            (Input.AssistancePrograms.Contains("None", StringComparer.OrdinalIgnoreCase) && Input.AssistancePrograms.Count > 1))
            ModelState.AddModelError("Input.AssistancePrograms", "Choose valid assistance programmes.");
        try { QuickIntakeVictimCircumstances.Normalize(Input.VictimCircumstances); }
        catch (ArgumentException exception)
        {
            ModelState.AddModelError("Input.VictimCircumstances", exception.Message);
        }
        if (!ModelState.IsValid)
            return BadRequest(new { error = "Please correct the required fields." });
        try
        {
            var result = await quickIntakeService.CreateAsync(
                new QuickIntakeSubmission(Input.FirstName, Input.LastName, Input.DateOfBirth!.Value,
                    Input.Gender, Input.MaritalStatus, Input.Telephone, Input.Email,
                    Input.StreetAddress, Input.CommunityDistrict, Input.Parish,
                    Input.EmploymentStatus, Input.MonthlyIncome,
                    string.Equals(Input.HasChildrenUnder18LivingFullTime, "Yes", StringComparison.OrdinalIgnoreCase),
                    string.Equals(Input.HasDisabilityOrSeriousMedicalCondition, "Yes", StringComparison.OrdinalIgnoreCase),
                    Input.AssistancePrograms, Input.HelpCategory, Input.SecondaryTelephone, Input.VictimCircumstances),
                HttpContext.RequestAborted, SubmissionId, await GetApplicantIdAsync());
            return new JsonResult(new { requestId = result.RequestId, reference = result.PublicReference });
        }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException) { return StatusCode(503, new { error = "Request service temporarily unavailable. Please retry." }); }
    }

    public async Task<IActionResult> OnPostUploadAsync()
    {
        if (RequestId == Guid.Empty || SubmissionId == Guid.Empty || RequestId != SubmissionId ||
            AttachmentFile is null || AttachmentIndex is < 0 or > 4)
            return BadRequest(new { error = "Invalid attachment upload." });
        if (AttachmentFile.Length <= 0 || AttachmentFile.Length > 10 * 1024 * 1024)
            return BadRequest(new { error = "Each attachment must be between 1 byte and 10 MB." });
        string extension = Path.GetExtension(AttachmentFile.FileName).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".pdf"))
            return BadRequest(new { error = "Only JPG, PNG, or PDF attachments are supported." });
        try
        {
            Guid uploader = await quickIntakeService.GetAnonymousUploaderIdAsync(RequestId, HttpContext.RequestAborted);
            await using var stream = AttachmentFile.OpenReadStream();
            await documentService.UploadForRequestAsync(uploader, RequestId, DocumentType.RequestSupport,
                AttachmentFile.FileName, AttachmentFile.ContentType, AttachmentFile.Length, stream, HttpContext.RequestAborted);
            return new JsonResult(new { uploaded = true });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            return StatusCode(503, new { error = "The attachment could not be saved. Please retry." });
        }
    }

    public sealed class InputModel
    {
        [Required, StringLength(150)]
        public string FirstName { get; set; } = string.Empty;

        [Required, StringLength(150)]
        public string LastName { get; set; } = string.Empty;

        public DateOnly? DateOfBirth { get; set; }

        [Required, RegularExpression("Male|Female|Prefer not to say")]
        public string Gender { get; set; } = string.Empty;

        [Required, RegularExpression("Single|Married|Divorced|Widowed")]
        public string MaritalStatus { get; set; } = string.Empty;

        [Required, StringLength(40)]
        public string Telephone { get; set; } = string.Empty;

        [StringLength(40)]
        public string? SecondaryTelephone { get; set; }

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

        public List<string> VictimCircumstances { get; set; } = new();

        public List<string> AssistancePrograms { get; set; } = new();

        [Required, RegularExpression("Food|Housing|Earn an Income|Education|Health|Water & Hygiene")]
        public string HelpCategory { get; set; } = string.Empty;

        public string? Website { get; set; }
    }
}
