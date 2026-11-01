namespace RequestPortal.Infrastructure.Messaging;

public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    public bool Enabled { get; set; }

    public string FfpEmailAddress { get; set; } = string.Empty;

    public bool WhatsAppEnabled { get; set; }

    public string SmtpHost { get; set; } = string.Empty;

    public int SmtpPort { get; set; } = 587;

    public bool EnableSsl { get; set; } = true;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "FFP Request Portal";

    public string? UserName { get; set; }

    public string PasswordSecretName { get; set; } = "Notifications:SmtpPassword";

    public string WhatsAppGraphApiVersion { get; set; } = "v23.0";

    public string WhatsAppPhoneNumberId { get; set; } = string.Empty;

    public string WhatsAppAccessTokenSecretName { get; set; } = "messaging/whatsapp/access-token";
}
