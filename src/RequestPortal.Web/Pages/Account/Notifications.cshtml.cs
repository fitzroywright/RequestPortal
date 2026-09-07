using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Notifications;
using RequestPortal.Domain.Notifications;
using RequestPortal.Infrastructure.Identity;

namespace RequestPortal.Web.Pages.Account;

[Authorize]
public sealed class NotificationsModel : PageModel
{
    private readonly UserManager<ApplicationUser> userManager;
    private readonly INotificationPreferenceService notificationPreferenceService;

    public NotificationsModel(
        UserManager<ApplicationUser> userManager,
        INotificationPreferenceService notificationPreferenceService)
    {
        this.userManager = userManager;
        this.notificationPreferenceService = notificationPreferenceService;
    }

    public IReadOnlyList<NotificationPreferenceView> Preferences { get; private set; } = Array.Empty<NotificationPreferenceView>();

    public IReadOnlyList<NotificationHistoryView> History { get; private set; } = Array.Empty<NotificationHistoryView>();

    public string DefaultEmail { get; private set; } = string.Empty;

    [BindProperty]
    public PreferenceInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        ApplicationUser user = await GetUserAsync();
        await LoadAsync(user);
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        ApplicationUser user = await GetUserAsync();

        if (!ModelState.IsValid)
        {
            await LoadAsync(user);
            return Page();
        }

        await notificationPreferenceService.SavePreferenceAsync(
            user.Id,
            Input.Channel,
            Input.Destination,
            Input.Enabled,
            HttpContext.RequestAborted);

        TempData["Success"] = $"{Input.Channel} notification preference saved.";
        return RedirectToPage();
    }

    private async Task<ApplicationUser> GetUserAsync()
    {
        return await userManager.GetUserAsync(User)
            ?? throw new InvalidOperationException("Authenticated portal user could not be resolved.");
    }

    private async Task LoadAsync(ApplicationUser user)
    {
        DefaultEmail = user.Email ?? string.Empty;
        Preferences = await notificationPreferenceService.GetPreferencesAsync(
            user.Id,
            HttpContext.RequestAborted);
        History = await notificationPreferenceService.GetRecentHistoryAsync(
            user.Id,
            25,
            HttpContext.RequestAborted);
    }

    public NotificationPreferenceView? FindPreference(NotificationChannel channel)
    {
        return Preferences.FirstOrDefault(preference => preference.Channel == channel);
    }

    public sealed class PreferenceInput
    {
        [Required]
        public NotificationChannel Channel { get; set; }

        [Required]
        [StringLength(500)]
        public string Destination { get; set; } = string.Empty;

        public bool Enabled { get; set; }
    }
}
