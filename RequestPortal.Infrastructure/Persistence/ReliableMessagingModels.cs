using System;

namespace RequestPortal.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    public Guid? RequestId { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public bool IsTerminal { get; set; }
    public void StopRetrying(string explanation)
    {
        IsTerminal = true;
        NextAttemptUtc = null;
        // Old workers do not know IsTerminal. Their existing encoded schedule parser must also
        // refuse this message after a binary rollback, without falsifying its attempt count.
        LastError = AttemptCount >= 10 ? $"DEAD LETTER: {explanation}"
            : $"RETRY_AT={DateTimeOffset.MaxValue:O}|TERMINAL: {explanation}";
    }
    public Guid Id { get; set; }

    public string MessageType { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public DateTimeOffset OccurredUtc { get; set; }

    public DateTimeOffset? ProcessedUtc { get; set; }

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }
}

public sealed class InboxMessage
{
    public Guid MessageId { get; set; }

    public string Consumer { get; set; } = string.Empty;

    public DateTimeOffset ReceivedUtc { get; set; }

    public DateTimeOffset? ProcessedUtc { get; set; }
}
