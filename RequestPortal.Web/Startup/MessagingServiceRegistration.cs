using RequestPortal.Application.Notifications;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Notifications;

namespace RequestPortal.Web.Startup;

internal static class MessagingServiceRegistration
{
    public static void Add(WebApplicationBuilder builder)
    {
        builder.Services
            .AddOptions<NotificationOptions>()
            .Bind(
                builder.Configuration.GetSection(
                    NotificationOptions.SectionName));

        NotificationOptions notificationOptions =
            builder.Configuration
                .GetSection(
                    NotificationOptions.SectionName)
                .Get<NotificationOptions>()
            ?? new NotificationOptions();

        string messagingQueuePath =
            builder.Configuration[
                "Messaging:DurableQueuePath"]
            ?? Path.Combine(
                "App_Data"
                , "common-messaging");

        Common.Messaging.Hosting.ServiceCollectionExtensions
            .AddCommonMessagingSecrets(
                builder.Services);

        AddMicrosoftGraphEmail(builder);
        AddMicrosoftGraphTeams(builder);
        AddSmtp(
            builder
            , notificationOptions);
        AddWhatsApp(
            builder
            , notificationOptions);
        AddQueuedDelivery(
            builder
            , messagingQueuePath);

        Common.Messaging.Hosting.ServiceCollectionExtensions
            .AddCommonMessagingDiagnostics(
                builder.Services);

        builder.Services.AddScoped<
            INotificationSender
            , CommonQueuedNotificationSender>();
    }

    private static void AddMicrosoftGraphEmail(
        WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue(
                "Messaging:MicrosoftGraph:Email:Enabled"
                , false))
        {
            return;
        }

        Common.Messaging.Hosting.ChannelServiceCollectionExtensions
            .AddMicrosoftGraphEmailMessagingChannel(
                builder.Services
                , new Common.Messaging.Channels.MicrosoftGraph
                    .MicrosoftGraphEmailOptions
                {
                    TenantId =
                        builder.Configuration[
                            "Messaging:MicrosoftGraph:TenantId"]
                        ?? string.Empty
                    , ClientId =
                        builder.Configuration[
                            "Messaging:MicrosoftGraph:ClientId"]
                        ?? string.Empty
                    , SenderUpn =
                        builder.Configuration[
                            "Messaging:MicrosoftGraph:Email:SenderUpn"]
                        ?? string.Empty
                    , ClientSecretName =
                        builder.Configuration[
                            "Messaging:MicrosoftGraph:ClientSecretName"]
                        ?? "messaging/msgraph/client-secret"
                });
    }

    private static void AddMicrosoftGraphTeams(
        WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue(
                "Messaging:MicrosoftGraph:Teams:Enabled"
                , false))
        {
            return;
        }

        Common.Messaging.Hosting.ChannelServiceCollectionExtensions
            .AddMicrosoftGraphTeamsMessagingChannel(
                builder.Services
                , new Common.Messaging.Channels.MicrosoftGraph
                    .MicrosoftGraphTeamsOptions
                {
                    SenderUpn =
                        builder.Configuration[
                            "Messaging:MicrosoftGraph:Teams:SenderUpn"]
                        ?? string.Empty
                    , DelegatedAccessTokenSecretName =
                        builder.Configuration[
                            "Messaging:MicrosoftGraph:Teams:DelegatedAccessTokenSecretName"]
                        ?? "messaging/msgraph/teams/delegated-access-token"
                });
    }

    private static void AddSmtp(
        WebApplicationBuilder builder
        , NotificationOptions options)
    {
        Common.Messaging.Hosting.ChannelServiceCollectionExtensions
            .AddSmtpMessagingChannel(
                builder.Services
                , new Common.Messaging.Channels.Smtp
                    .SmtpMessageOptions
                {
                    Host = options.SmtpHost ?? string.Empty
                    , Port = options.SmtpPort
                    , EnableSsl = options.EnableSsl
                    , FromAddress =
                        options.FromAddress ?? string.Empty
                    , UserName =
                        options.UserName ?? string.Empty
                    , PasswordSecretName =
                        options.PasswordSecretName
                });
    }

    private static void AddWhatsApp(
        WebApplicationBuilder builder
        , NotificationOptions options)
    {
        if (!options.WhatsAppEnabled)
        {
            return;
        }

        Common.Messaging.Hosting.ChannelServiceCollectionExtensions
            .AddWhatsAppMessagingChannel(
                builder.Services
                , new Common.Messaging.Channels.WhatsApp
                    .WhatsAppMessageOptions
                {
                    Endpoint = options.WhatsAppEndpoint
                    , EndpointSecretName =
                        options.WhatsAppEndpointSecretName
                    , ApiTokenSecretName =
                        options.WhatsAppApiTokenSecretName
                });
    }

    private static void AddQueuedDelivery(
        WebApplicationBuilder builder
        , string messagingQueuePath)
    {
        Common.Messaging.Hosting.ServiceCollectionExtensions
            .AddCommonMessagingQueuedDelivery(
                builder.Services
                , new Common.Messaging.ExternalDeliveryOptions
                {
                    DeliveryMode =
                        Common.Messaging.ExternalDeliveryMode
                            .ParallelByRecipient
                    , MaxConcurrency = 4
                    , MaxAttempts = 5
                    , InitialRetryDelay =
                        TimeSpan.FromSeconds(5)
                    , MaxRetryDelay =
                        TimeSpan.FromMinutes(5)
                    , DurableQueuePath =
                        messagingQueuePath
                }
                , durable: true);
    }
}
