using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Organizations;
using RequestPortal.Domain.Documents;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Organizations;

public sealed class ProfileModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IOrganizationProfileService organizationProfileService;
    private readonly IDocumentService documentService;

    public ProfileModel(
        UserManager<ApplicationUser> userManager,
        IOrganizationProfileService organizationProfileService,
        IDocumentService documentService)
    {
        this.userManager = userManager;
        this.organizationProfileService = organizationProfileService;
        this.documentService = documentService;
    }

    public OrganizationProfile? Organization { get; private set; }

    [BindProperty]
    public ProfileInput Input { get; set; } = new();

    [BindProperty]
    public IFormFile? RegistrationCertificate { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();
        Organization = await organizationProfileService.GetAsync(user.Id, id, HttpContext.RequestAborted);
        if (Organization is null)
        {
            return NotFound();
        }

        Input = new ProfileInput
        {
            RegistrationNumber = Organization.RegistrationNumber,
            TaxRegistrationNumber = Organization.TaxRegistrationNumber,
            OrganizationType = Organization.OrganizationType,
            TradingName = Organization.TradingName,
            DateEstablished = Organization.DateEstablished,
            CountryOfRegistration = Organization.CountryOfRegistration ?? "Jamaica",
            Address = Organization.Address,
            Parish = Organization.Parish,
            DistrictArea = Organization.DistrictArea,
            MailingAddress = Organization.MailingAddress,
            Phone = Organization.Phone,
            AlternativePhone = Organization.AlternativePhone,
            Email = Organization.Email,
            Website = Organization.Website,
            PrimaryContactName = Organization.PrimaryContactName,
            PrimaryContactTitle = Organization.PrimaryContactTitle,
            AuthorizedSignatoryName = Organization.AuthorizedSignatoryName,
            AuthorizedSignatoryTitle = Organization.AuthorizedSignatoryTitle,
            SecondaryContactName = Organization.SecondaryContactName,
            SecondaryContactEmail = Organization.SecondaryContactEmail,
            SecondaryContactPhone = Organization.SecondaryContactPhone,
            EmployeeCount = Organization.EmployeeCount,
            VolunteerCount = Organization.VolunteerCount,
            ApproximateBeneficiariesServed = Organization.ApproximateBeneficiariesServed,
            GeographicServiceArea = Organization.GeographicServiceArea,
            MainActivities = Organization.MainActivities,
            PopulationGroupsServed = Organization.PopulationGroupsServed,
            PreviouslyReceivedFfpAssistance = Organization.PreviouslyReceivedFfpAssistance,
            AnnualOperatingBudget = Organization.AnnualOperatingBudget,
            PrimaryFundingSources = Organization.PrimaryFundingSources,
            PremisesSituation = Organization.PremisesSituation,
            RegistrationExpiryDate = Organization.RegistrationExpiryDate
        };

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();

        if (!ModelState.IsValid)
        {
            Organization = await organizationProfileService.GetAsync(user.Id, id, HttpContext.RequestAborted);
            return Page();
        }

        await organizationProfileService.UpdateAsync(
            user.Id,
            id,
            new OrganizationProfileUpdate(
                Input.RegistrationNumber,
                Input.TaxRegistrationNumber,
                Input.OrganizationType,
                Input.TradingName,
                Input.DateEstablished,
                Input.CountryOfRegistration,
                Input.Address,
                Input.Parish,
                Input.DistrictArea,
                Input.MailingAddress,
                Input.Phone,
                Input.AlternativePhone,
                Input.Email,
                Input.Website,
                Input.PrimaryContactName,
                Input.PrimaryContactTitle,
                Input.AuthorizedSignatoryName,
                Input.AuthorizedSignatoryTitle,
                Input.SecondaryContactName,
                Input.SecondaryContactEmail,
                Input.SecondaryContactPhone,
                Input.EmployeeCount,
                Input.VolunteerCount,
                Input.ApproximateBeneficiariesServed,
                Input.GeographicServiceArea,
                Input.MainActivities,
                Input.PopulationGroupsServed,
                Input.PreviouslyReceivedFfpAssistance,
                Input.AnnualOperatingBudget,
                Input.PrimaryFundingSources,
                Input.PremisesSituation,
                Input.RegistrationExpiryDate),
            HttpContext.RequestAborted);

        TempData["Success"] = "Organization profile updated.";
        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage(new { id, returnUrl = ReturnUrl });
    }

    public async Task<IActionResult> OnPostUploadCertificateAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();

        if (RegistrationCertificate is null || RegistrationCertificate.Length <= 0)
        {
            TempData["Error"] = "Select a registration certificate to upload.";
            return RedirectToPage(new { id, returnUrl = ReturnUrl });
        }

        await using System.IO.Stream content = RegistrationCertificate.OpenReadStream();
        await documentService.UploadForOrganizationAsync(
            user.Id,
            id,
            DocumentType.OrganizationRegistrationCertificate,
            RegistrationCertificate.FileName,
            RegistrationCertificate.ContentType ?? "application/octet-stream",
            RegistrationCertificate.Length,
            content,
            HttpContext.RequestAborted);

        TempData["Success"] = "Registration certificate uploaded.";
        return RedirectToPage(new { id, returnUrl = ReturnUrl });
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }

    public sealed class ProfileInput
    {
        [Required, StringLength(100)]
        public string? RegistrationNumber { get; set; }

        [StringLength(100)]
        public string? TaxRegistrationNumber { get; set; }

        [Required, StringLength(100)]
        public string? OrganizationType { get; set; }

        [StringLength(250)]
        public string? TradingName { get; set; }

        public DateOnly? DateEstablished { get; set; }

        [Required, StringLength(100)]
        public string CountryOfRegistration { get; set; } = "Jamaica";

        [Required, StringLength(1000)]
        public string? Address { get; set; }

        [Required, StringLength(100)]
        public string? Parish { get; set; }

        [StringLength(200)]
        public string? DistrictArea { get; set; }

        [StringLength(1000)]
        public string? MailingAddress { get; set; }

        [Required, StringLength(100)]
        public string? Phone { get; set; }

        [StringLength(100)]
        public string? AlternativePhone { get; set; }

        [Required, EmailAddress, StringLength(320)]
        public string? Email { get; set; }

        [Url, StringLength(500)]
        public string? Website { get; set; }

        [Required, StringLength(250)]
        public string? PrimaryContactName { get; set; }

        [Required, StringLength(150)]
        public string? PrimaryContactTitle { get; set; }

        [StringLength(250)]
        public string? AuthorizedSignatoryName { get; set; }

        [StringLength(150)]
        public string? AuthorizedSignatoryTitle { get; set; }

        [StringLength(250)]
        public string? SecondaryContactName { get; set; }

        [EmailAddress, StringLength(320)]
        public string? SecondaryContactEmail { get; set; }

        [StringLength(100)]
        public string? SecondaryContactPhone { get; set; }

        [Range(0, 1000000)]
        public int? EmployeeCount { get; set; }

        [Range(0, 1000000)]
        public int? VolunteerCount { get; set; }

        [Range(0, 100000000)]
        public int? ApproximateBeneficiariesServed { get; set; }

        [StringLength(1000)]
        public string? GeographicServiceArea { get; set; }

        [Required, StringLength(4000, MinimumLength = 10)]
        public string? MainActivities { get; set; }

        [StringLength(2000)]
        public string? PopulationGroupsServed { get; set; }

        public bool? PreviouslyReceivedFfpAssistance { get; set; }

        [Range(typeof(decimal), "0", "999999999999999")]
        public decimal? AnnualOperatingBudget { get; set; }

        [StringLength(2000)]
        public string? PrimaryFundingSources { get; set; }

        [StringLength(500)]
        public string? PremisesSituation { get; set; }

        public DateOnly? RegistrationExpiryDate { get; set; }
    }
}
