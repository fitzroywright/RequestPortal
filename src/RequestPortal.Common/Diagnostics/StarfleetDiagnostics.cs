using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Common.Diagnostics;

public enum StarfleetSeverity
{
    Trace = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
    Critical = 4
}

public sealed record StarfleetDiagnosticEvent(
    Guid EventId,
    DateTimeOffset OccurredUtc,
    string Component,
    string Code,
    StarfleetSeverity Severity,
    string Message,
    string? Detail = null,
    string? CorrelationId = null);

public interface IStarfleetDiagnosticSink
{
    Task WriteAsync(StarfleetDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default);
}

public interface IStarfleetDiagnostics
{
    Task ReportAsync(
        string component,
        string code,
        StarfleetSeverity severity,
        string message,
        string? detail = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default);
}

public sealed class StarfleetDiagnostics : IStarfleetDiagnostics
{
    private readonly IReadOnlyList<IStarfleetDiagnosticSink> sinks;

    public StarfleetDiagnostics(IEnumerable<IStarfleetDiagnosticSink> sinks)
    {
        this.sinks = new List<IStarfleetDiagnosticSink>(sinks);
    }

    public async Task ReportAsync(
        string component,
        string code,
        StarfleetSeverity severity,
        string message,
        string? detail = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        StarfleetDiagnosticEvent diagnosticEvent = new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            component,
            code,
            severity,
            message,
            detail,
            correlationId);

        foreach (IStarfleetDiagnosticSink sink in sinks)
        {
            await sink.WriteAsync(diagnosticEvent, cancellationToken);
        }
    }
}
