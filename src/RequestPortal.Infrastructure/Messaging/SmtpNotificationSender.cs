using System;
using System.Net;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Secrets;

namespace RequestPortal.Infrastructure.Messaging;

public sealed class SmtpNotificationSender : INotificationSender
{
    private readonly NotificationOptions options;
    private readonly ISecretProvider secretProvider;
    private readonly ILogger<SmtpNotificationSender> logger;

    public SmtpNotificationSender(
        IOptions<NotificationOptions> options,
        ISecretProvider secretProvider,
        ILogger<SmtpNotificationSender> logger)
    {
        this.options = options.Value;
        this.secretProvider = secretProvider;
        this.logger = logger;
    }

    public async Task SendAsync(NotificationEnvelope notification, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            logger.LogInformation(
                "Notification delivery disabled. Recipient={Recipient} Subject={Subject}",
                notification.Recipient,
                notification.Subject);
            return;
        }

        if (string.IsNullOrWhiteSpace(options.SmtpHost) || string.IsNullOrWhiteSpace(options.FromAddress))
        {
            throw new InvalidOperationException("Notification SMTP host and from address must be configured when notifications are enabled.");
        }

        using MailMessage message = new();
        message.From = new MailAddress(options.FromAddress, options.FromName);
        message.To.Add(notification.Recipient);
        message.Subject = notification.Subject;
        message.Body = notification.Body;
        message.IsBodyHtml = notification.IsHtml;

        using SmtpClient client = new(options.SmtpHost, options.SmtpPort);
        client.EnableSsl = options.EnableSsl;

        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            string? password = await secretProvider.GetSecretAsync(options.PasswordSecretName, cancellationToken);
            if (string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException($"SMTP password secret '{options.PasswordSecretName}' is not available.");
            }

            client.Credentials = new NetworkCredential(options.UserName, password);
        }

        await client.SendMailAsync(message, cancellationToken);
    }
}
