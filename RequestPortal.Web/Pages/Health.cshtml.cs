using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RequestPortal.Web.Pages;

[AllowAnonymous]
public sealed class HealthModel : PageModel
{
    public IActionResult OnGet()
    {
        return new JsonResult(new
        {
            status = "running",
            application = "RequestPortal",
            timeUtc = DateTimeOffset.UtcNow
        });
    }
}
