using System;

namespace RequestPortal.Domain.Notifications;

public enum NotificationDeliveryStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    Skipped = 3
}

public sealed class NotificationHistory
{
    private NotificationHistory()
    {
    }

    public NotificationHistory(
        Guid id,
        Guid userId,
        Guid? organizationId,
        Guid? requestId,
        NotificationChannel channel,
        string destination,
        string subject,
        DateTimeOffset createdUtc)
    {
        Id = id;
        UserId = userId;
        OrganizationId = organizationId;
        RequestId = requestId;
        Channel = channel;
        Destination = destination.Trim();
        Subject = subject.Trim();
        CreatedUtc = createdUtc;
        Status = NotificationDeliveryStatus.Pending;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid? OrganizationId { get; private set; }

    public Guid? RequestId { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public string Destination { get; private set; } = string.Empty;

    public string Subject { get; private set; } = string.Empty;

    public NotificationDeliveryStatus Status { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset? SentUtc { get; private set; }

    public string? LastError { get; private set; }

    public void MarkSent(DateTimeOffset sentUtc)
    {
        Status = NotificationDeliveryStatus.Sent;
        SentUtc = sentUtc;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        Status = NotificationDeliveryStatus.Failed;
        LastError = string.IsNullOrWhiteSpace(error) ? "Unknown delivery error." : error.Trim();
    }

    public void MarkSkipped(string reason)
    {
        Status = NotificationDeliveryStatus.Skipped;
        LastError = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }
}
