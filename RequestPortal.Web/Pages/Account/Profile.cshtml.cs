using System.ComponentModel.DataAnnotations;
using Common.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Countries;
using RequestPortal.Application.Notifications;
using RequestPortal.Application.Organizations;
using RequestPortal.Application.Portal;
using RequestPortal.Domain.Notifications;
using RequestPortal.Infrastructure.Documents;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Account;

[Authorize(Policy = "PortalRepresentative")]
public sealed class ProfileModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly ICountryPolicyService countryPolicyService;
    private readonly INotificationPreferenceService notificationPreferenceService;
    private readonly IOrganizationInvitationService invitationService;
    private readonly IConfiguration configuration;
    private readonly IFileStorage fileStorage;
    private readonly IDocumentUploadSecurity uploadSecurity;
    private readonly IMalwareScanner malwareScanner;

    public ProfileModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        ICountryPolicyService countryPolicyService,
        INotificationPreferenceService notificationPreferenceService,
        IOrganizationInvitationService invitationService,
        IConfiguration configuration,
        IFileStorage fileStorage,
        IDocumentUploadSecurity uploadSecurity,
        IMalwareScanner malwareScanner)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.countryPolicyService = countryPolicyService;
        this.notificationPreferenceService = notificationPreferenceService;
        this.invitationService = invitationService;
        this.configuration = configuration;
        this.fileStorage = fileStorage;
        this.uploadSecurity = uploadSecurity;
        this.malwareScanner = malwareScanner;
    }

    public IReadOnlyList<OrganizationSummary> Organizations { get; private set; } = Array.Empty<OrganizationSummary>();
    public IReadOnlyList<OrganizationInvitationView> PendingInvitations { get; private set; } = Array.Empty<OrganizationInvitationView>();
    public IReadOnlyList<CountryOption> RegistrationCountries { get; private set; } = Array.Empty<CountryOption>();
    public IReadOnlyList<NotificationPreferenceView> Preferences { get; private set; } = Array.Empty<NotificationPreferenceView>();
    public IReadOnlyList<OrganizationNotificationSubscriptionView> OrganizationSubscriptions { get; private set; } = Array.Empty<OrganizationNotificationSubscriptionView>();
    public bool EmailAvailable { get; private set; }
    public bool WhatsAppAvailable { get; private set; }
    public string RegisteredIdentity { get; private set; } = string.Empty;
    public bool IndividualProfileUpdateRequired { get; private set; }
    public bool HasProfilePhoto { get; private set; }
    public Guid UserId { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public PersonalProfileInput ProfileInput { get; set; } = new();

    [BindProperty]
    public PreferenceInput Preference { get; set; } = new();

    [BindProperty]
    public OrganizationSubscriptionInput OrganizationInput { get; set; } = new();

    [BindProperty]
    public RegistrationInput Registration { get; set; } = new();

    [BindProperty]
    public IFormFile? ProfilePhoto { get; set; }

    public async Task OnGetAsync()
    {
        ApplicationUser user = await GetUserAsync();
        await LoadAsync(user);
    }

    public async Task<IActionResult> OnPostProfileAsync()
    {
        ApplicationUser user = await GetUserAsync();
        KeepValidationFor(nameof(ProfileInput));

        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        user.DisplayName = ProfileInput.DisplayName.Trim();
        user.Email = ProfileInput.Email.Trim();
        user.PhoneNumber = string.IsNullOrWhiteSpace(ProfileInput.PhoneNumber)
            ? null
            : ProfileInput.PhoneNumber.Trim();
        user.DateOfBirth = ProfileInput.DateOfBirth;
        user.Gender = ProfileInput.Gender.Trim();
        user.NationalIdTrn = ProfileInput.NationalIdTrn.Trim();
        user.MaritalStatus = ProfileInput.MaritalStatus.Trim();
        user.HomeAddress = ProfileInput.HomeAddress.Trim();
        user.HomeAddressLine2 = Normalize(ProfileInput.HomeAddressLine2);
        user.Town = ProfileInput.Town.Trim();
        user.Parish = ProfileInput.Parish.Trim();
        user.DistrictArea = ProfileInput.DistrictArea.Trim();
        user.AlternativePhoneNumber = Normalize(ProfileInput.AlternativePhoneNumber);
        user.EmploymentStatus = ProfileInput.EmploymentStatus.Trim();
        user.EmployerInstitution = Normalize(ProfileInput.EmployerInstitution);
        user.MonthlyHouseholdIncome = ProfileInput.MonthlyHouseholdIncome;
        user.HouseholdSize = ProfileInput.HouseholdSize;
        user.NumberOfDependants = ProfileInput.NumberOfDependants;
        user.ChildrenUnder18 = ProfileInput.ChildrenUnder18;
        user.HousingSituation = ProfileInput.HousingSituation.Trim();
        user.HouseholdLivingSituation = ProfileInput.HouseholdLivingSituation.Trim();
        user.ReferralSource = ProfileInput.ReferralSource.Trim();
        user.IndividualProfileUpdateRequired = !user.IsIndividualProfileComplete;
        user.IndividualProfileUpdateRequiredUtc = user.IndividualProfileUpdateRequired
            ? DateTimeOffset.UtcNow
            : null;

        IdentityResult result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            foreach (IdentityError error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);

            await LoadAsync(user);
            return Page();
        }

        TempData["Success"] = "Profile updated.";
        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPhotoAsync()
    {
        ApplicationUser user = await GetUserAsync();
        KeepValidationFor(nameof(ProfilePhoto));

        if (ProfilePhoto is null || ProfilePhoto.Length == 0)
        {
            ModelState.AddModelError(nameof(ProfilePhoto), "Choose a PNG or JPEG photo.");
            await LoadAsync(user);
            return Page();
        }

        if (ProfilePhoto.Length > 5L * 1024L * 1024L)
        {
            ModelState.AddModelError(nameof(ProfilePhoto), "Profile photos must be 5 MB or smaller.");
            await LoadAsync(user);
            return Page();
        }

        string extension = Path.GetExtension(ProfilePhoto.FileName);
        if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(ProfilePhoto), "Profile photos must be PNG or JPEG.");
            await LoadAsync(user);
            return Page();
        }

        await using Stream content = ProfilePhoto.OpenReadStream();
        await uploadSecurity.ValidateAsync(
            ProfilePhoto.FileName,
            ProfilePhoto.ContentType,
            ProfilePhoto.Length,
            content,
            HttpContext.RequestAborted);
        await malwareScanner.ScanAsync(
            content,
            HttpContext.RequestAborted);

        string safeName = Path.GetFileName(ProfilePhoto.FileName);
        string storageKey =
            $"requestportal/profile-photos/{user.Id:N}/{Guid.NewGuid():N}-{safeName}";
        string? previousKey = user.ProfilePhotoStorageKey;

        StoredFile stored = await fileStorage.StoreAsync(
            new StorageWriteRequest(
                storageKey,
                content,
                ProfilePhoto.ContentType,
                safeName,
                user.Id.ToString("D"),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Application"] = "RequestPortal",
                    ["OwnerType"] = "ApplicantProfile",
                    ["UserId"] = user.Id.ToString("D")
                }),
            HttpContext.RequestAborted);

        user.ProfilePhotoStorageKey = stored.StorageKey;
        user.ProfilePhotoContentType = stored.ContentType;
        user.ProfilePhotoUpdatedUtc = DateTimeOffset.UtcNow;

        IdentityResult result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            await fileStorage.DeleteAsync(stored.StorageKey, HttpContext.RequestAborted);
            foreach (IdentityError error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);

            await LoadAsync(user);
            return Page();
        }

        if (!string.IsNullOrWhiteSpace(previousKey) &&
            !string.Equals(previousKey, stored.StorageKey, StringComparison.Ordinal))
        {
            await fileStorage.DeleteAsync(previousKey, HttpContext.RequestAborted);
        }

        TempData["Success"] = "Profile photo updated.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemovePhotoAsync()
    {
        ApplicationUser user = await GetUserAsync();
        string? previousKey = user.ProfilePhotoStorageKey;

        user.ProfilePhotoStorageKey = null;
        user.ProfilePhotoContentType = null;
        user.ProfilePhotoUpdatedUtc = null;

        IdentityResult result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            foreach (IdentityError error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);

            await LoadAsync(user);
            return Page();
        }

        if (!string.IsNullOrWhiteSpace(previousKey))
        {
            await fileStorage.DeleteAsync(previousKey, HttpContext.RequestAborted);
        }

        TempData["Success"] = "Profile photo removed.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPreferenceAsync()
    {
        ApplicationUser user = await GetUserAsync();
        KeepValidationFor(nameof(Preference));
        LoadAvailability();
        IndividualProfileUpdateRequired = user.IndividualProfileUpdateRequired;

        if (Preference.Enabled && !IsAvailable(Preference.Channel))
            ModelState.AddModelError(string.Empty, $"{Preference.Channel} notifications are not available.");

        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        await notificationPreferenceService.SavePreferenceAsync(
            user.Id,
            Preference.Channel,
            Preference.Destination,
            Preference.Enabled,
            HttpContext.RequestAborted);

        TempData["Success"] = $"{Preference.Channel} notification preference saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostOrganizationSubscriptionAsync()
    {
        ApplicationUser user = await GetUserAsync();

        await notificationPreferenceService.SaveOrganizationSubscriptionAsync(
            user.Id,
            OrganizationInput.MembershipId,
            OrganizationInput.ReceiveAllOrganizationActivity,
            OrganizationInput.ReceiveAllOrganizationActivity,
            OrganizationInput.ReceiveAllOrganizationActivity,
            HttpContext.RequestAborted);

        TempData["Success"] = "Organization notification preferences saved.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRegisterOrganizationAsync()
    {
        ApplicationUser user = await GetUserAsync();
        KeepValidationFor(nameof(Registration));

        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        await countryPolicyService.EnsureOrganizationRegistrationAllowedAsync(
            Registration.CountryCode,
            HttpContext.RequestAborted);

        await workflowService.RegisterOrganizationAsync(
            user.Id,
            Registration.Name,
            Registration.CountryCode,
            HttpContext.RequestAborted);

        TempData["Success"] = "Organization registration submitted to FFP for approval.";
        return RedirectToPage();
    }

    public NotificationPreferenceView? FindPreference(NotificationChannel channel) =>
        Preferences.FirstOrDefault(item => item.Channel == channel);

    private async Task<ApplicationUser> GetUserAsync() =>
        await userManager.GetUserAsync(User)
        ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");

    private async Task LoadAsync(ApplicationUser user, bool preserveInput = false)
    {
        LoadAvailability();

        if (!preserveInput)
        {
            ProfileInput = new PersonalProfileInput
            {
                DisplayName = user.DisplayName,
                Email = user.Email ?? string.Empty,
                PhoneNumber = user.PhoneNumber,
                DateOfBirth = user.DateOfBirth,
                Gender = user.Gender ?? string.Empty,
                NationalIdTrn = user.NationalIdTrn ?? string.Empty,
                MaritalStatus = user.MaritalStatus ?? string.Empty,
                HomeAddress = user.HomeAddress ?? string.Empty,
                HomeAddressLine2 = user.HomeAddressLine2,
                Town = user.Town ?? string.Empty,
                Parish = user.Parish ?? string.Empty,
                DistrictArea = user.DistrictArea ?? string.Empty,
                AlternativePhoneNumber = user.AlternativePhoneNumber,
                EmploymentStatus = user.EmploymentStatus ?? string.Empty,
                EmployerInstitution = user.EmployerInstitution,
                MonthlyHouseholdIncome = user.MonthlyHouseholdIncome,
                HouseholdSize = user.HouseholdSize,
                NumberOfDependants = user.NumberOfDependants,
                ChildrenUnder18 = user.ChildrenUnder18,
                HousingSituation = user.HousingSituation ?? string.Empty,
                HouseholdLivingSituation = user.HouseholdLivingSituation ?? string.Empty,
                ReferralSource = user.ReferralSource ?? string.Empty
            };
        }

        UserId = user.Id;
        HasProfilePhoto = !string.IsNullOrWhiteSpace(user.ProfilePhotoStorageKey);

        RegisteredIdentity =
            user.UserName
            ?? User.FindFirst("email")?.Value
            ?? user.Email
            ?? "External identity";

        await notificationPreferenceService.EnsureDefaultEmailPreferenceAsync(
            user.Id,
            user.Email,
            HttpContext.RequestAborted);

        Preferences = await notificationPreferenceService.GetPreferencesAsync(
            user.Id,
            HttpContext.RequestAborted);
        OrganizationSubscriptions = await notificationPreferenceService.GetOrganizationSubscriptionsAsync(
            user.Id,
            HttpContext.RequestAborted);
        Organizations = await workflowService.GetOrganizationsForUserAsync(
            user.Id,
            HttpContext.RequestAborted);
        PendingInvitations = await invitationService.GetPendingForUserAsync(
            user.Id,
            user.Email,
            HttpContext.RequestAborted);

        IReadOnlyList<CountryOption> countries =
            await countryPolicyService.GetActiveCountriesAsync(HttpContext.RequestAborted);

        RegistrationCountries = countries
            .Where(item => item.OrganizationRegistrationEnabled)
            .ToArray();
    }

    private void LoadAvailability()
    {
        EmailAvailable =
            configuration.GetValue("Notifications:Enabled", false) ||
            configuration.GetValue("Messaging:MicrosoftGraph:Email:Enabled", false);
        WhatsAppAvailable =
            configuration.GetValue("Notifications:WhatsAppEnabled", false);
    }

    private void KeepValidationFor(string prefix)
    {
        foreach (string key in ModelState.Keys.Where(key =>
            !key.StartsWith(prefix + ".", StringComparison.Ordinal) && key != prefix).ToArray())
            ModelState.Remove(key);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private bool IsAvailable(NotificationChannel channel) =>
        channel switch
        {
            NotificationChannel.Email => EmailAvailable,
            NotificationChannel.WhatsApp => WhatsAppAvailable,
            _ => false
        };

    public sealed class PersonalProfileInput
    {
        [Required, StringLength(250, MinimumLength = 2)]
        public string DisplayName { get; set; } = string.Empty;

        [Required, EmailAddress, StringLength(320)]
        public string Email { get; set; } = string.Empty;

        [Required, Phone, StringLength(100)]
        public string? PhoneNumber { get; set; }

        [Required]
        public DateOnly? DateOfBirth { get; set; }

        [Required, StringLength(50)]
        public string Gender { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string NationalIdTrn { get; set; } = string.Empty;

        [Required, StringLength(50)]
        public string MaritalStatus { get; set; } = string.Empty;

        [Required, StringLength(500)]
        public string HomeAddress { get; set; } = string.Empty;

        [StringLength(500)]
        public string? HomeAddressLine2 { get; set; }

        [Required, StringLength(150)]
        public string Town { get; set; } = string.Empty;

        [Required, StringLength(100)]
        public string Parish { get; set; } = string.Empty;

        [Required, StringLength(200)]
        public string DistrictArea { get; set; } = string.Empty;

        [Phone, StringLength(100)]
        public string? AlternativePhoneNumber { get; set; }

        [Required, StringLength(100)]
        public string EmploymentStatus { get; set; } = string.Empty;

        [StringLength(250)]
        public string? EmployerInstitution { get; set; }

        [Required, Range(typeof(decimal), "0", "999999999999")]
        public decimal? MonthlyHouseholdIncome { get; set; }

        [Required, Range(1, 100)]
        public int? HouseholdSize { get; set; }

        [Required, Range(0, 100)]
        public int? NumberOfDependants { get; set; }

        [Required, Range(0, 100)]
        public int? ChildrenUnder18 { get; set; }

        [Required, StringLength(100)]
        public string HousingSituation { get; set; } = string.Empty;

        [Required, StringLength(4000, MinimumLength = 5)]
        public string HouseholdLivingSituation { get; set; } = string.Empty;

        [Required, StringLength(150)]
        public string ReferralSource { get; set; } = string.Empty;
    }

    public sealed class PreferenceInput
    {
        [Required]
        public NotificationChannel Channel { get; set; }

        [Required, StringLength(500)]
        public string Destination { get; set; } = string.Empty;

        public bool Enabled { get; set; }
    }

    public sealed class OrganizationSubscriptionInput
    {
        public Guid MembershipId { get; set; }
        public bool ReceiveAllOrganizationActivity { get; set; }
    }

    public sealed class RegistrationInput
    {
        [Required, StringLength(250, MinimumLength = 3)]
        public string Name { get; set; } = string.Empty;

        [Required, StringLength(2, MinimumLength = 2)]
        public string CountryCode { get; set; } = "JM";
    }
}
