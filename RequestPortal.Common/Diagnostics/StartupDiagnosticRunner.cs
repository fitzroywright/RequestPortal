using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RequestPortal.Common.Diagnostics;

public sealed class StartupDiagnosticRunner
{
    private readonly IReadOnlyList<IStartupDiagnostic> diagnostics;
    private readonly ILogger<StartupDiagnosticRunner> logger;

    public StartupDiagnosticRunner(
        IEnumerable<IStartupDiagnostic> diagnostics,
        ILogger<StartupDiagnosticRunner> logger)
    {
        this.diagnostics = new List<IStartupDiagnostic>(diagnostics);
        this.logger = logger;
    }

    public async Task<IReadOnlyList<DiagnosticCheckResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        List<DiagnosticCheckResult> results = new();

        foreach (IStartupDiagnostic diagnostic in diagnostics)
        {
            DiagnosticCheckResult result = await diagnostic.CheckAsync(cancellationToken);
            results.Add(result);

            if (result.IsHealthy)
            {
                logger.LogInformation(
                    "Startup diagnostic {DiagnosticName} completed successfully. {Detail}",
                    result.Name,
                    result.Detail);
            }
            else
            {
                logger.LogError(
                    "Startup diagnostic {DiagnosticName} failed. {Detail}",
                    result.Name,
                    result.Detail);
            }
        }

        return results;
    }
}
