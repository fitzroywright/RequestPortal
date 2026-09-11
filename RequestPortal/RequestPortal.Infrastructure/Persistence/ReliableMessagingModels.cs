using System;

namespace RequestPortal.Infrastructure.Persistence;

public sealed class OutboxMessage
{
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
