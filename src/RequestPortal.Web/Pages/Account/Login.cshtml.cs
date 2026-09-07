using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Common.Time;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Web.Demo;

namespace RequestPortal.Web.Pages.Account;

public sealed class LoginModel : PageModel
{
    private static readonly string[] supportedProviders = ["Google", "Microsoft", "Facebook"];

    private readonly SignInManager<ApplicationUser> signInManager;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IAuthenticationSchemeProvider schemeProvider;
    private readonly IConfiguration configuration;
    private readonly IClock clock;
    private readonly IStarfleetDiagnosticSink diagnosticSink;

    public LoginModel(
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthenticationSchemeProvider schemeProvider,
        IConfiguration configuration,
        IClock clock,
        IStarfleetDiagnosticSink diagnosticSink)
    {
        this.signInManager = signInManager;
        this.userManager = userManager;
        this.schemeProvider = schemeProvider;
        this.configuration = configuration;
        this.clock = clock;
        this.diagnosticSink = diagnosticSink;
    }

    public IReadOnlyList<string> ExternalProviders { get; private set; } = Array.Empty<string>();

    public bool DemoEnabled { get; private set; }

    public string ReturnUrl { get; private set; } = "/Dashboard";

    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(string? returnUrl = null)
    {
        ReturnUrl = LocalReturnUrl(returnUrl);
        await LoadProvidersAsync();
    }

    public IActionResult OnPostExternal(string provider, string? returnUrl = null)
    {
        if (!supportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest("Unsupported external identity provider.");
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
            ErrorMessage = "The external sign-in provider could not complete authentication.";
            await LoadProvidersAsync();
            return Page();
        }

        ExternalLoginInfo? loginInfo = await signInManager.GetExternalLoginInfoAsync();
        if (loginInfo is null)
        {
            ErrorMessage = "External sign-in information was not available.";
            await LoadProvidersAsync();
            return Page();
        }

        Microsoft.AspNetCore.Identity.SignInResult existingResult = await signInManager.ExternalLoginSignInAsync(
            loginInfo.LoginProvider,
            loginInfo.ProviderKey,
            isPersistent: false,
            bypassTwoFactor: false);

        if (existingResult.Succeeded)
        {
            await EmitAsync("SF-AUTH-EXTERNAL-OK", "External representative authentication succeeded.", loginInfo.LoginProvider);
            return LocalRedirect(ReturnUrl);
        }

        string? email = loginInfo.Principal.FindFirstValue(ClaimTypes.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            ErrorMessage = "The identity provider did not supply an email address.";
            await LoadProvidersAsync();
            return Page();
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
                ErrorMessage = "The Request Portal account could not be created.";
                await EmitAsync("SF-AUTH-EXTERNAL-LOCALUSER-FAIL", "External user local account creation failed.", string.Join("; ", createResult.Errors.Select(error => error.Code)));
                await LoadProvidersAsync();
                return Page();
            }
        }

        IdentityResult loginResult = await userManager.AddLoginAsync(user, loginInfo);
        if (!loginResult.Succeeded && !loginResult.Errors.Any(error => error.Code == "LoginAlreadyAssociated"))
        {
            ErrorMessage = "The external sign-in could not be linked to the Request Portal account.";
            await LoadProvidersAsync();
            return Page();
        }

        user.LastSignInUtc = clock.UtcNow;
        await userManager.UpdateAsync(user);
        await signInManager.SignInAsync(user, isPersistent: false);
        await EmitAsync("SF-AUTH-EXTERNAL-OK", "External representative authentication succeeded.", loginInfo.LoginProvider);

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
            ErrorMessage = "Demo data is not initialized. Start the application with Demo:EnsureDatabase enabled once.";
            await LoadProvidersAsync();
            return Page();
        }

        user.LastSignInUtc = clock.UtcNow;
        await userManager.UpdateAsync(user);
        await signInManager.SignInAsync(user, isPersistent: false);
        await EmitAsync("SF-AUTH-DEMO-REP", "Demo representative signed in.", demoEmail);

        return LocalRedirect(LocalReturnUrl(returnUrl));
    }

    private async Task LoadProvidersAsync()
    {
        IEnumerable<AuthenticationScheme> schemes = await schemeProvider.GetAllSchemesAsync();
        ExternalProviders = schemes
            .Where(scheme => supportedProviders.Contains(scheme.Name, StringComparer.OrdinalIgnoreCase))
            .Select(scheme => scheme.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name)
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

    private Task EmitAsync(string code, string message, string detail)
    {
        StarfleetDiagnosticEvent diagnosticEvent = new(
            Guid.NewGuid(),
            clock.UtcNow,
            "Security.Authentication",
            code,
            StarfleetSeverity.Information,
            message,
            detail,
            HttpContext.TraceIdentifier);

        return diagnosticSink.WriteAsync(diagnosticEvent, HttpContext.RequestAborted);
    }
}
