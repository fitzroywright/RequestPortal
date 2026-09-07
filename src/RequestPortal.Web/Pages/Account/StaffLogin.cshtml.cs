using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Web.Demo;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

[AllowAnonymous]
public sealed class StaffLoginModel : PageModel
{
    private readonly StaffSignInService staffSignInService;
    private readonly SignInManager<ApplicationUser> signInManager;
    private readonly UserManager<ApplicationUser> userManager;
    private readonly IConfiguration configuration;

    public StaffLoginModel(
        StaffSignInService staffSignInService,
        SignInManager<ApplicationUser> signInManager,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration)
    {
        this.staffSignInService = staffSignInService;
        this.signInManager = signInManager;
        this.userManager = userManager;
        this.configuration = configuration;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public bool DemoEnabled { get; private set; }

    public void OnGet()
    {
        DemoEnabled = configuration.GetValue("Demo:Enabled", false);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        DemoEnabled = configuration.GetValue("Demo:Enabled", false);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        StaffSignInResult result = await staffSignInService.SignInAsync(
            Input.UserName,
            Input.Password,
            cancellationToken);

        Input.Password = string.Empty;

        if (!result.Succeeded)
        {
            ErrorMessage = result.FailureCode == "NOT_AUTHORIZED_FOR_REQUEST_PORTAL"
                ? "Your FFP account is valid, but it is not authorized for Request Portal staff access."
                : "The FFP username or password could not be authenticated.";
            return Page();
        }

        return RedirectToPage("/Staff/Index");
    }

    public async Task<IActionResult> OnPostDemoAsync()
    {
        DemoEnabled = configuration.GetValue("Demo:Enabled", false);
        if (!DemoEnabled)
        {
            return NotFound();
        }

        ApplicationUser? user = await userManager.FindByEmailAsync(DemoDataSeeder.StaffEmail);
        if (user is null)
        {
            ErrorMessage = "Demo staff data is not initialized.";
            return Page();
        }

        await signInManager.SignInAsync(user, isPersistent: false);
        return RedirectToPage("/Staff/Index");
    }

    public sealed class InputModel
    {
        [Required]
        [Display(Name = "FFP Username")]
        public string UserName { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
