using System;

namespace RequestPortal.Domain.Notifications;

public enum NotificationChannel
{
    Email = 0,
    Sms = 1,
    WhatsApp = 2
}

public sealed class NotificationPreference
{
    private NotificationPreference()
    {
    }

    public NotificationPreference(Guid id, Guid userId, NotificationChannel channel, string destination, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("Notification destination is required.", nameof(destination));
        }

        Id = id;
        UserId = userId;
        Channel = channel;
        Destination = destination.Trim();
        Enabled = enabled;
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public string Destination { get; private set; } = string.Empty;

    public bool Enabled { get; private set; }

    public void Update(string destination, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("Notification destination is required.", nameof(destination));
        }

        Destination = destination.Trim();
        Enabled = enabled;
    }

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
    }
}
