using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Demo;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

[AllowAnonymous]
public sealed class StaffLoginModel : PageModel
{
    private readonly StaffOidcOptions oidcOptions;
    private readonly IConfiguration configuration;
    private readonly UserManager<ApplicationUser> userManager;

    public StaffLoginModel(
        IOptions<StaffOidcOptions> oidcOptions,
        IConfiguration configuration,
        UserManager<ApplicationUser> userManager)
    {
        this.oidcOptions = oidcOptions.Value;
        this.configuration = configuration;
        this.userManager = userManager;
    }

    public bool IsDemo => configuration.GetValue("Demo:Enabled", false);

    [BindProperty]
    public string UserName { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public IActionResult OnGet()
    {
        if (IsDemo)
        {
            return Page();
        }

        if (!oidcOptions.Enabled)
        {
            return RedirectToPage("/Account/StaffAccessDenied", new { reason = "not-configured" });
        }

        AuthenticationProperties properties = new()
        {
            RedirectUri = Url.Page("/Account/StaffComplete") ?? "/Account/StaffComplete",
            IsPersistent = false
        };
        return Challenge(properties, StaffOidcOptions.Scheme);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IsDemo)
        {
            return RedirectToPage();
        }

        string expectedUserName = configuration["Demo:StaffUsername"] ?? "FWright";
        string expectedPassword = configuration["Demo:StaffPassword"] ?? string.Empty;
        if (!FixedTimeEquals(UserName.Trim(), expectedUserName) ||
            string.IsNullOrEmpty(expectedPassword) ||
            !FixedTimeEquals(Password, expectedPassword))
        {
            ModelState.AddModelError(string.Empty, "Invalid demo staff username or password.");
            return Page();
        }

        ApplicationUser? staff = await userManager.FindByEmailAsync(DemoDataSeeder.StaffEmail);
        if (staff is null)
        {
            ModelState.AddModelError(string.Empty, "Demo staff data has not been initialized. Restart the demo and try again.");
            return Page();
        }

        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, staff.Id.ToString()),
            new Claim(ClaimTypes.Name, expectedUserName),
            new Claim(ClaimTypes.Email, DemoDataSeeder.StaffEmail),
            new Claim(ClaimTypes.Role, RequestPortalRoles.Reviewer),
            new Claim(ClaimTypes.Role, RequestPortalRoles.Administrator),
            new Claim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue),
            new Claim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access),
            new Claim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ReviewRequests),
            new Claim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ViewManagementDashboard),
            new Claim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity)
        ];

        ClaimsIdentity identity = new(
            claims,
            StaffOidcOptions.StaffCookieScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        await HttpContext.SignInAsync(
            StaffOidcOptions.StaffCookieScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false });

        return RedirectToPage("/Management/Index");
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        byte[] leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        byte[] rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }
}
