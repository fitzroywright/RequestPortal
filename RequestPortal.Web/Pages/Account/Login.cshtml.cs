using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Common.Diagnostics;
using Common.Security.Abstractions;
using Common.Security.Hosting;
using Common.Security.Services;
using RequestPortal.Web.Security;
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

    private readonly IHostEnvironment environment;
    private readonly RequestPortalAccounts accounts;
    private readonly SignInManager<ApplicationUser> signInManager;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IAuthenticationSchemeProvider schemeProvider;
    private readonly IConfiguration configuration;
    private readonly IClock clock;
    private readonly IDiagnosticOperationsRouter diagnostics;
    private readonly INotificationPreferenceService notificationPreferences;

    public LoginModel(
        IHostEnvironment environment,
        RequestPortalAccounts accounts,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IAuthenticationSchemeProvider schemeProvider,
        IConfiguration configuration,
        IClock clock,
        IDiagnosticOperationsRouter diagnostics,
        INotificationPreferenceService notificationPreferences)
    {
        this.environment=environment;
        this.accounts = accounts;
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
            TempData["Error"] = "We couldn’t start that sign-in. Please choose one of the available sign-in methods and try again.";
            await EmitAsync("RP-AUTH-EXTERNAL-UNSUPPORTED", "Unsupported external identity provider was requested.", provider ?? string.Empty);
            return RedirectToPage("/Index");
        }

        IEnumerable<AuthenticationScheme> schemes = await schemeProvider.GetAllSchemesAsync();
        bool configured = schemes.Any(scheme =>
            string.Equals(scheme.Name, provider, StringComparison.OrdinalIgnoreCase));

        if (!configured)
        {
            TempData["Error"] = $"We couldn’t sign you in with {FriendlyProvider(provider)}. Please try another sign-in method.";
            await EmitAsync("RP-AUTH-EXTERNAL-NOTCONFIGURED", "External identity provider is not configured.", provider);
            return RedirectToPage("/Index");
        }

        string redirectUrl = Url.Page(
            "/Account/Login",
            "ExternalCallback",
            new
            {
                returnUrl = LocalReturnUrl(returnUrl),
                provider
            }) ?? "/Dashboard";

        AuthenticationProperties properties = signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);
        return Challenge(properties, provider);
    }

    public async Task<IActionResult> OnGetExternalCallbackAsync(
        string? returnUrl = null,
        string? remoteError = null,
        string? provider = null)
    {
        ReturnUrl = LocalReturnUrl(returnUrl);
        string providerName = FriendlyProvider(provider);

        if (!string.IsNullOrWhiteSpace(remoteError))
        {
            TempData["Error"] = $"We couldn’t sign you in with {providerName}. Please try again, or choose another sign-in method.";
            await EmitAsync("RP-AUTH-EXTERNAL-REMOTE-FAIL", "External identity provider returned an authentication error.", remoteError);
            return RedirectToPage("/Index");
        }

        ExternalLoginInfo? loginInfo = await signInManager.GetExternalLoginInfoAsync();
        if (loginInfo is null)
        {
            TempData["Error"] = $"We couldn’t sign you in with {providerName}. Please try again, or choose another sign-in method.";
            await EmitAsync("RP-AUTH-EXTERNAL-INFO-MISSING", "External sign-in information was unavailable.", provider ?? string.Empty);
            return RedirectToPage("/Index");
        }

        providerName = FriendlyProvider(loginInfo.LoginProvider);

        try
        {
            var proof=PublicExternalIdentity.FromCallback(loginInfo);
            var account=new AccountProvisioningService(accounts.Public).Provision(proof,AccountPopulation.PublicApplicant).Account;
            if(account.Status!=SecurityAccountStatus.Active) return Forbid();
            var user=await userManager.FindByIdAsync(account.Id);
            if(user is null || user.IsInternalStaff) return Forbid();
            user.LastSignInUtc=clock.UtcNow;
            var updated=await userManager.UpdateAsync(user);
            if(!updated.Succeeded) throw new InvalidOperationException("Unable to update the applicant sign-in record.");
            await notificationPreferences.EnsureDefaultEmailPreferenceAsync(user.Id,user.Email,HttpContext.RequestAborted);
            var claims=AccountSecurityHosting.SessionClaims(account,proof).Concat(new[] {
                new Claim(ClaimTypes.Name,user.DisplayName),new Claim(ClaimTypes.GivenName,user.DisplayName) });
            await signInManager.SignInWithClaimsAsync(user,false,claims);
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            await EmitAsync("RP-AUTH-EXTERNAL-OK","External applicant authentication succeeded.",loginInfo.LoginProvider);
            return LocalRedirect(ReturnUrl);
        }
        catch(IdentityEnrollmentRequiredException)
        {
            TempData["Error"]="This account needs an approved identity link. Sign in using your existing sign-in method and link the additional provider, or contact support.";
            await EmitAsync("RP-AUTH-EXTERNAL-LINK-REQUIRED","Identity enrollment requires approval.",loginInfo.LoginProvider);
            return RedirectToPage("/Index");
        }
    }

    public async Task<IActionResult> OnPostDemoAsync(string persona, string? returnUrl = null)
    {
        DemoEnabled = configuration.GetValue("Demo:Enabled", false);
        if (!global::Common.Security.Authenticators.MockEnvironmentPolicy.IsPermitted(DemoEnabled,environment.EnvironmentName,configuration.GetValue("Demo:IsolationConfirmed",false)))
            return NotFound();

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

        if(user.IsInternalStaff)return Forbid();
        var proof=new global::Common.Security.Authenticators.MockAuthenticator(new global::Common.Security.Options.MockAuthenticationOptions {
            Enabled=true,EnvironmentName=environment.EnvironmentName,IsolationConfirmed=true,ApplicationId="RequestPortal.Public",
            Identities=[new(user.UserName!,user.Id.ToString("D"),user.DisplayName)] }).Authenticate(user.UserName!,"").Identity!;
        var account=accounts.Public.ImportApprovedAccounts([new AccountCreation(user.Id.ToString("D"),user.UserName!,user.DisplayName,
            global::Common.Security.Models.AuthenticationProviderKind.Mock,AccountPopulation.PublicApplicant,SecurityAccountStatus.Active,proof.Key)],"isolated-demo-seeder")[0];
        if(account.Status!=SecurityAccountStatus.Active)return Forbid();
        user.LastSignInUtc = clock.UtcNow;
        await userManager.UpdateAsync(user);
        await notificationPreferences.EnsureDefaultEmailPreferenceAsync(
            user.Id,
            user.Email,
            HttpContext.RequestAborted);
        await signInManager.SignInWithClaimsAsync(
            user,
            isPersistent: false,
            AccountSecurityHosting.SessionClaims(account,proof).Concat(new[]
            {
                new Claim(ClaimTypes.Name, user.DisplayName ?? "User"),
                new Claim(ClaimTypes.GivenName, user.DisplayName ?? "User")
            }));
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

    private static string FriendlyProvider(string? provider)
    {
        string? supported = supportedProviders.FirstOrDefault(item =>
            string.Equals(item, provider, StringComparison.OrdinalIgnoreCase));

        return supported ?? "that sign-in method";
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
