using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Common.Diagnostics;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using RequestPortal.Application.Notifications;
using RequestPortal.Common.Time;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Web.Demo;

namespace RequestPortal.Web.Pages.Account;

public sealed class LoginModel : PageModel
{
    private static readonly string[] supportedProviders = ["Google", "Facebook", "Yahoo", "Microsoft", "Apple"];

    private readonly SignInManager<ApplicationUser> signInManager;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IAuthenticationSchemeProvider schemeProvider;
    private readonly IConfiguration configuration;
    private readonly IClock clock;
    private readonly IDiagnosticOperationsRouter diagnostics;
    private readonly INotificationPreferenceService notificationPreferences;

    public LoginModel(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthenticationSchemeProvider schemeProvider,
        IConfiguration configuration,
        IClock clock,
        IDiagnosticOperationsRouter diagnostics,
        INotificationPreferenceService notificationPreferences)
    {
        this.signInManager = signInManager;
        this.userManager = userManager;
        this.schemeProvider = schemeProvider;
        this.configuration = configuration;
        this.clock = clock;
        this.diagnostics = diagnostics;
        this.notificationPreferences = notificationPreferences;
    }

    public IReadOnlyList<ExternalProviderOption> ExternalProviders { get; private set; } = Array.Empty<ExternalProviderOption>();

    public bool DemoEnabled { get; private set; }

    public string ReturnUrl { get; private set; } = "/Dashboard";

    public string? ErrorMessage { get; private set; }

    public IActionResult OnGet(string? returnUrl = null)
    {
        string target = string.IsNullOrWhiteSpace(returnUrl)
            ? "/"
            : $"/?returnUrl={Uri.EscapeDataString(LocalReturnUrl(returnUrl))}";
        return LocalRedirect(target);
    }

    public async Task<IActionResult> OnPostExternalAsync(string provider, string? returnUrl = null)
    {
        if (!supportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest("Unsupported external identity provider.");
        }

        IEnumerable<AuthenticationScheme> schemes = await schemeProvider.GetAllSchemesAsync();
        bool configured = schemes.Any(scheme =>
            string.Equals(scheme.Name, provider, StringComparison.OrdinalIgnoreCase));

        if (!configured)
        {
            TempData["Error"] = $"{provider} sign-in is not configured yet.";
            return RedirectToPage("/Index");
        }

        string redirectUrl = Url.Page(
            "/Account/Login",
            "ExternalCallback",
            new { returnUrl = LocalReturnUrl(returnUrl) }) ?? "/Dashboard";

        AuthenticationProperties properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    public async Task<IActionResult> OnGetExternalCallbackAsync(string? returnUrl = null, string? remoteError = null)
    {
        ReturnUrl = LocalReturnUrl(returnUrl);

        if (!string.IsNullOrWhiteSpace(remoteError))
        {
            TempData["Error"] = "The external sign-in provider could not complete authentication.";
            return RedirectToPage("/Index");
        }

        ExternalLoginInfo? loginInfo = await signInManager.GetExternalLoginInfoAsync();
        if (loginInfo is null)
        {
            TempData["Error"] = "External sign-in information was not available.";
            return RedirectToPage("/Index");
        }

        Microsoft.AspNetCore.Identity.SignInResult existingResult = await signInManager.ExternalLoginSignInAsync(
            loginInfo.LoginProvider,
            loginInfo.ProviderKey,
            isPersistent: false,
            bypassTwoFactor: false);

        if (existingResult.Succeeded)
        {
            ApplicationUser? existingUser = await userManager.FindByLoginAsync(
                loginInfo.LoginProvider,
                loginInfo.ProviderKey);
            if (existingUser is not null)
            {
                await notificationPreferences.EnsureDefaultEmailPreferenceAsync(
                    existingUser.Id,
                    existingUser.Email,
                    HttpContext.RequestAborted);
            }

            if (existingUser is null)
            {
                TempData["Error"] = "The external sign-in account could not be found.";
                return RedirectToPage("/Index");
            }

            await signInManager.SignInWithClaimsAsync(
                existingUser,
                isPersistent: false,
                new[]
                {
                    new Claim(ClaimTypes.Name, existingUser.DisplayName ?? existingUser.Email ?? "User"),
                    new Claim(ClaimTypes.GivenName, existingUser.DisplayName ?? existingUser.Email ?? "User")
                });
            await EmitAsync("RP-AUTH-EXTERNAL-OK", "External representative authentication succeeded.", loginInfo.LoginProvider);
            return LocalRedirect(ReturnUrl);
        }

        string? email = loginInfo.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            TempData["Error"] = "The identity provider did not supply the required account identity.";
            return RedirectToPage("/Index");
        }

        ApplicationUser? user = await userManager.FindByEmailAsync(email);
        if (user is null)
        {
            string displayName = loginInfo.Principal.FindFirstValue(ClaimTypes.Name) ?? email;
            user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = displayName,
                IsInternalStaff = false,
                CreatedUtc = clock.UtcNow,
                LastSignInUtc = clock.UtcNow
            };

            IdentityResult createResult = await userManager.CreateAsync(user);
            if (!createResult.Succeeded)
            {
                TempData["Error"] = "The Request Portal account could not be created.";
                await EmitAsync("RP-AUTH-EXTERNAL-LOCALUSER-FAIL", "External user local account creation failed.", string.Join("; ", createResult.Errors.Select(error => error.Code)));
                return RedirectToPage("/Index");
            }
        }

        IdentityResult loginResult = await userManager.AddLoginAsync(user, loginInfo);
        if (!loginResult.Succeeded && !loginResult.Errors.Any(error => error.Code == "LoginAlreadyAssociated"))
        {
            TempData["Error"] = "The external sign-in could not be linked to the Request Portal account.";
            return RedirectToPage("/Index");
        }

        user.LastSignInUtc = clock.UtcNow;
        await userManager.UpdateAsync(user);
        await notificationPreferences.EnsureDefaultEmailPreferenceAsync(
            user.Id,
            user.Email,
            HttpContext.RequestAborted);
        await signInManager.SignInWithClaimsAsync(
            user,
            isPersistent: false,
            new[]
            {
                new Claim(ClaimTypes.Name, user.DisplayName ?? user.Email ?? "User"),
                new Claim(ClaimTypes.GivenName, user.DisplayName ?? user.Email ?? "User")
            });
        await EmitAsync("RP-AUTH-EXTERNAL-OK", "External representative authentication succeeded.", loginInfo.LoginProvider);

        return LocalRedirect(ReturnUrl);
    }

    public async Task<IActionResult> OnPostDemoAsync(string persona, string? returnUrl = null)
    {
        DemoEnabled = configuration.GetValue("Demo:Enabled", false);
        if (!DemoEnabled)
        {
            return NotFound();
        }

        string demoEmail = persona switch
        {
            "admin" => DemoDataSeeder.AdministratorEmail,
            "pending" => DemoDataSeeder.PendingRepresentativeEmail,
            _ => DemoDataSeeder.RepresentativeEmail
        };

        ApplicationUser? user = await userManager.FindByEmailAsync(demoEmail);
        if (user is null)
        {
            TempData["Error"] = "Demo data is not initialized.";
            return RedirectToPage("/Index");
        }

        user.LastSignInUtc = clock.UtcNow;
        await userManager.UpdateAsync(user);
        await notificationPreferences.EnsureDefaultEmailPreferenceAsync(
            user.Id,
            user.Email,
            HttpContext.RequestAborted);
        await signInManager.SignInWithClaimsAsync(
            user,
            isPersistent: false,
            new[]
            {
                new Claim(ClaimTypes.Name, user.DisplayName ?? "User"),
                new Claim(ClaimTypes.GivenName, user.DisplayName ?? "User")
            });
        await EmitAsync("RP-AUTH-DEMO-REP", "Demo representative signed in.", demoEmail);

        return LocalRedirect(LocalReturnUrl(returnUrl));
    }

    private async Task LoadProvidersAsync()
    {
        IEnumerable<AuthenticationScheme> schemes = await schemeProvider.GetAllSchemesAsync();
        HashSet<string> configuredProviders = schemes
            .Select(scheme => scheme.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        ExternalProviders = supportedProviders
            .Select(provider => new ExternalProviderOption(
                provider,
                configuredProviders.Contains(provider)))
            .ToArray();

        DemoEnabled = configuration.GetValue("Demo:Enabled", false);
    }

    private string LocalReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return returnUrl;
        }

        return "/Dashboard";
    }

    public sealed record ExternalProviderOption(string Name, bool IsConfigured);

    private Task EmitAsync(string code, string message, string detail)
    {
        DiagnosticTelemetryEvent telemetryEvent = new(
            clock.UtcNow,
            "Security.Authentication",
            code,
            DiagnosticSeverity.Information,
            message,
            HttpContext.TraceIdentifier,
            new Dictionary<string, string>
            {
                ["Detail"] = detail
            });

        return diagnostics.RouteAsync(telemetryEvent, HttpContext.RequestAborted);
    }
}
