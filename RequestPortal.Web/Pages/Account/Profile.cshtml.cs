using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Countries;
using RequestPortal.Application.Notifications;
using RequestPortal.Application.Portal;
using RequestPortal.Domain.Notifications;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Account;

[Authorize(Policy = "PortalRepresentative")]
public sealed class ProfileModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IPortalWorkflowService workflowService;
    private readonly ICountryPolicyService countryPolicyService;
    private readonly INotificationPreferenceService notificationPreferenceService;
    private readonly IConfiguration configuration;

    public ProfileModel(
        UserManager<ApplicationUser> userManager,
        IPortalWorkflowService workflowService,
        ICountryPolicyService countryPolicyService,
        INotificationPreferenceService notificationPreferenceService,
        IConfiguration configuration)
    {
        this.userManager = userManager;
        this.workflowService = workflowService;
        this.countryPolicyService = countryPolicyService;
        this.notificationPreferenceService = notificationPreferenceService;
        this.configuration = configuration;
    }

    public IReadOnlyList<OrganizationSummary> Organizations { get; private set; } = Array.Empty<OrganizationSummary>();
    public IReadOnlyList<OrganizationSummary> AvailableOrganizations { get; private set; } = Array.Empty<OrganizationSummary>();
    public IReadOnlyList<CountryOption> RegistrationCountries { get; private set; } = Array.Empty<CountryOption>();
    public IReadOnlyList<NotificationPreferenceView> Preferences { get; private set; } = Array.Empty<NotificationPreferenceView>();
    public IReadOnlyList<OrganizationNotificationSubscriptionView> OrganizationSubscriptions { get; private set; } = Array.Empty<OrganizationNotificationSubscriptionView>();
    public bool EmailAvailable { get; private set; }
    public bool WhatsAppAvailable { get; private set; }
    public string RegisteredIdentity { get; private set; } = string.Empty;
    public bool IndividualProfileUpdateRequired { get; private set; }

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
            await LoadAsync(user, preserveInput: true);
            return Page();
        }

        user.DisplayName = ProfileInput.DisplayName.Trim();
        user.Email = ProfileInput.Email.Trim();
        user.PhoneNumber = string.IsNullOrWhiteSpace(ProfileInput.PhoneNumber)
            ? null
            : ProfileInput.PhoneNumber.Trim();
        user.IndividualProfileUpdateRequired = false;
        user.IndividualProfileUpdateRequiredUtc = null;

        IdentityResult result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            foreach (IdentityError error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);

            await LoadAsync(user, preserveInput: true);
            return Page();
        }

        TempData["Success"] = "Profile updated.";
        if (!string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return LocalRedirect(ReturnUrl);
        }

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
            await LoadAsync(user, preserveInput: true);
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

    public async Task<IActionResult> OnPostRequestMembershipAsync(Guid organizationId)
    {
        ApplicationUser user = await GetUserAsync();
        await workflowService.RequestMembershipAsync(
            user.Id,
            organizationId,
            HttpContext.RequestAborted);

        TempData["Success"] = "Membership request submitted.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRegisterOrganizationAsync()
    {
        ApplicationUser user = await GetUserAsync();
        KeepValidationFor(nameof(Registration));

        if (!ModelState.IsValid)
        {
            await LoadAsync(user, preserveInput: true);
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
                PhoneNumber = user.PhoneNumber
            };
        }

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
        AvailableOrganizations = await workflowService.GetAvailableOrganizationsAsync(
            user.Id,
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

        [Phone, StringLength(100)]
        public string? PhoneNumber { get; set; }
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
