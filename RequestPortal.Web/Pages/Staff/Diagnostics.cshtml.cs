using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Infrastructure.Diagnostics;

namespace RequestPortal.Web.Pages.Staff;

public sealed class DiagnosticsModel : PageModel
{
    private readonly StartupDiagnosticRunner diagnosticRunner;

    public DiagnosticsModel(StartupDiagnosticRunner diagnosticRunner)
    {
        this.diagnosticRunner = diagnosticRunner;
    }

    public IReadOnlyList<DiagnosticCheckResult> Results { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Results = await diagnosticRunner.RunAsync(HttpContext.RequestAborted);
    }
}
