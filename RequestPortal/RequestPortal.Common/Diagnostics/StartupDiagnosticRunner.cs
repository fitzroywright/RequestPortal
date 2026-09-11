using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Common.Diagnostics;

public sealed class StartupDiagnosticRunner
{
    private readonly IReadOnlyList<IStartupDiagnostic> diagnostics;
    private readonly IStarfleetDiagnostics starfleetDiagnostics;

    public StartupDiagnosticRunner(
        IEnumerable<IStartupDiagnostic> diagnostics,
        IStarfleetDiagnostics starfleetDiagnostics)
    {
        this.diagnostics = new List<IStartupDiagnostic>(diagnostics);
        this.starfleetDiagnostics = starfleetDiagnostics;
    }

    public async Task<IReadOnlyList<DiagnosticCheckResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        List<DiagnosticCheckResult> results = new();

        foreach (IStartupDiagnostic diagnostic in diagnostics)
        {
            DiagnosticCheckResult result = await diagnostic.CheckAsync(cancellationToken);
            results.Add(result);

            await starfleetDiagnostics.ReportAsync(
                "Startup",
                result.IsHealthy ? "SF-STARTUP-OK" : "SF-STARTUP-FAIL",
                result.IsHealthy ? StarfleetSeverity.Information : StarfleetSeverity.Critical,
                $"Startup diagnostic '{result.Name}' completed.",
                result.Detail,
                cancellationToken: cancellationToken);
        }

        return results;
    }
}
