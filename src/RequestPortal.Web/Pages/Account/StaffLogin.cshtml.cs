using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

[AllowAnonymous]
public sealed class StaffLoginModel : PageModel
{
    private readonly StaffSignInService staffSignInService;

    public StaffLoginModel(StaffSignInService staffSignInService)
    {
        this.staffSignInService = staffSignInService;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
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
