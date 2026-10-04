using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RequestPortal.Web.Pages.Staff;

public sealed class OperationsModel : PageModel
{
    public IActionResult OnGet()
    {
        return RedirectToPage("/Staff/Index");
    }

    public IActionResult OnPost()
    {
        return RedirectToPage("/Staff/Index");
    }
}
