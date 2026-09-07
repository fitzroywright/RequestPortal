namespace RequestPortal.Common.Diagnostics;

public sealed record DiagnosticCheckResult(string Name, bool IsHealthy, string? Detail = null);

public interface IStartupDiagnostic
{
    string Name { get; }

    Task<DiagnosticCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}
