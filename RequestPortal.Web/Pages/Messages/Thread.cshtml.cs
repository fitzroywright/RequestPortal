using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Application.Messages;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Messages;

public sealed class ThreadModel(IPortalMessageService messageService) : PageModel
{
    public MessageThreadView? Thread { get; private set; }

    public bool IsStaff { get; private set; }

    [BindProperty]
    [Required]
    [StringLength(10000)]
    public string Reply { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        Guid userId = CurrentUserId();
        IsStaff = IsFfpStaff();
        Thread = await messageService.GetThreadAsync(
            userId,
            IsStaff,
            id,
            HttpContext.RequestAborted);
        return Thread is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        Guid userId = CurrentUserId();
        IsStaff = IsFfpStaff();

        if (!ModelState.IsValid)
        {
            Thread = await messageService.GetThreadAsync(
                userId,
                IsStaff,
                id,
                HttpContext.RequestAborted);
            return Page();
        }

        await messageService.ReplyAsync(
            userId,
            IsStaff,
            id,
            Reply,
            HttpContext.RequestAborted);

        return RedirectToPage(new { id });
    }

    private Guid CurrentUserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Authenticated user ID is unavailable."));

    private bool IsFfpStaff() =>
        User.HasClaim(StaffOidcOptions.StaffSurfaceClaim, StaffOidcOptions.StaffSurfaceValue);
}
