using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RequestPortal.Application.Integration;

/// <summary>
/// Bounded, allowlisted exception diagnostics for security administrators.
/// Never persists raw exception messages, remote response bodies, connection strings,
/// file system paths, payloads, stack source paths or arbitrary exception.ToString().
/// The full original exception remains in restricted server-side logging.
/// </summary>
public static class IntegrationExceptionDiagnostics
{
    private static readonly Regex SafeSymbol = new(@"[^a-zA-Z0-9_.+\\u0060]", RegexOptions.Compiled);
    private static string Symbol(string? value, int limit = 100)
    {
        string filtered = SafeSymbol.Replace(value ?? "unknown", "");
        return filtered[..Math.Min(limit, filtered.Length)];
    }

    public static string Capture(Exception exception, string stage, string? publicErrorCode = null)
    {
        var text = new StringBuilder(512);
        text.Append("Exception: ").AppendLine(Symbol(exception.GetType().Name));
        text.Append("Stage: ").AppendLine(Symbol(stage));
        if (!string.IsNullOrWhiteSpace(publicErrorCode))
            text.Append("Error code: ").AppendLine(Symbol(publicErrorCode));
        if (exception is IntegrationTransferException transfer)
            text.Append("Classified reason: ").AppendLine(transfer.Failure.Code);
        if (exception is ExternalApplicationValidationException validation)
            text.Append("Rejected field: ").AppendLine(Symbol(validation.Field));
        for (Exception? inner = exception.InnerException; inner is not null && text.Length < 1800; inner = inner.InnerException)
        {
            text.Append("Inner exception: ").AppendLine(Symbol(inner.GetType().Name));
        }

        text.AppendLine("Stack frames (method names only):");
        foreach (StackFrame frame in new StackTrace(exception, false).GetFrames() ?? [])
        {
            var method = frame.GetMethod();
            if (method is null) continue;
            text.Append("  at ")
                .Append(Symbol(method.DeclaringType?.FullName, 140))
                .Append('.')
                .AppendLine(Symbol(method.Name, 100));
            if (text.Length > 3000) break;
        }
        if (text.Length < 180) text.AppendLine("The stack trace was not available at capture time.");
        text.AppendLine("Raw exception text and file paths are available only in restricted service logs.");
        string result = text.ToString();
        return result[..Math.Min(result.Length, 4000)];
    }

    // Only accept the restricted diagnostic character set from an authenticated bridge.
    // Reject unexpected output rather than trying to redact arbitrary remote payloads.
    public static bool IsSafeTransportDetail(string? detail) =>
        detail is { Length: > 0 and <= 4000 } &&
        detail.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '\n' or '\r' or ':' or '.' or '_' or '+' or '-' or '(' or ')' or '\u0060');
}
