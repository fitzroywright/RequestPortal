using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace RequestPortal.Common.Diagnostics;

public sealed class LoggingDiagnosticSink : IStarfleetDiagnosticSink
{
    private readonly ILogger<LoggingDiagnosticSink> logger;

    public LoggingDiagnosticSink(ILogger<LoggingDiagnosticSink> logger)
    {
        this.logger = logger;
    }

    public Task WriteAsync(StarfleetDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default)
    {
        LogLevel logLevel = diagnosticEvent.Severity switch
        {
            StarfleetSeverity.Trace => LogLevel.Trace,
            StarfleetSeverity.Information => LogLevel.Information,
            StarfleetSeverity.Warning => LogLevel.Warning,
            StarfleetSeverity.Error => LogLevel.Error,
            StarfleetSeverity.Critical => LogLevel.Critical,
            _ => LogLevel.Information
        };

        logger.Log(
            logLevel,
            "Starfleet {Code} [{Component}] {Message} Detail={Detail} CorrelationId={CorrelationId} EventId={EventId}",
            diagnosticEvent.Code,
            diagnosticEvent.Component,
            diagnosticEvent.Message,
            diagnosticEvent.Detail,
            diagnosticEvent.CorrelationId,
            diagnosticEvent.EventId);

        return Task.CompletedTask;
    }
}
