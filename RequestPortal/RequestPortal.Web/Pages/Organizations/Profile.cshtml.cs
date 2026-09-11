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
            OrganizationType = Organization.OrganizationType,
            Address = Organization.Address,
            Phone = Organization.Phone,
            Email = Organization.Email,
            PrimaryContactName = Organization.PrimaryContactName,
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
            Input.RegistrationNumber,
            Input.OrganizationType,
            Input.Address,
            Input.Phone,
            Input.Email,
            Input.PrimaryContactName,
            Input.RegistrationExpiryDate,
            HttpContext.RequestAborted);

        TempData["Success"] = "Organization profile updated.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostUploadCertificateAsync(Guid id)
    {
        ApplicationUser user = await GetUserAsync();

        if (RegistrationCertificate is null || RegistrationCertificate.Length <= 0)
        {
            TempData["Error"] = "Select a registration certificate to upload.";
            return RedirectToPage(new { id });
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
        return RedirectToPage(new { id });
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }

    public sealed class ProfileInput
    {
        [StringLength(100)]
        public string? RegistrationNumber { get; set; }

        [StringLength(100)]
        public string? OrganizationType { get; set; }

        [StringLength(1000)]
        public string? Address { get; set; }

        [StringLength(100)]
        public string? Phone { get; set; }

        [EmailAddress]
        [StringLength(320)]
        public string? Email { get; set; }

        [StringLength(250)]
        public string? PrimaryContactName { get; set; }

        public DateOnly? RegistrationExpiryDate { get; set; }
    }
}
