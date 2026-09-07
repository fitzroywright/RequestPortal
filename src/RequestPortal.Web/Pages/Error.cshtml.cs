using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RequestPortal.Web.Pages;

public sealed class ErrorModel : PageModel
{
    public string? RequestId { get; private set; }

    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
    }
}
