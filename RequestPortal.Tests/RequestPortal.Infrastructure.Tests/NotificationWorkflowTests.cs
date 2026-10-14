using System.Text.Json;
using Common.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Notifications;
using RequestPortal.Application.Portal;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Notifications;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Notifications;
using RequestPortal.Infrastructure.Persistence;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class NotificationWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 23, 45, 0, TimeSpan.Zero);

    [Fact]
    public async Task DefaultEmailPreference_UsesRegisteredEmail_AndDoesNotOverwriteUserChoice()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        ApplicationUser user = CreateUser("representative@example.com");
        database.Db.Users.Add(user);
        await database.Db.SaveChangesAsync(cancellationToken);

        NotificationPreferenceService service = CreatePreferenceService(database.Db);

        await service.EnsureDefaultEmailPreferenceAsync(
            user.Id,
            user.Email,
            cancellationToken);

        NotificationPreferenceView initial = Assert.Single(
            await service.GetPreferencesAsync(user.Id, cancellationToken));

        Assert.Equal(NotificationChannel.Email, initial.Channel);
        Assert.Equal("representative@example.com", initial.Destination);
        Assert.True(initial.Enabled);

        await service.SavePreferenceAsync(
            user.Id,
            NotificationChannel.Email,
            "alternate@example.com",
            enabled: false,
            cancellationToken);

        await service.EnsureDefaultEmailPreferenceAsync(
            user.Id,
            user.Email,
            cancellationToken);

        NotificationPreferenceView saved = Assert.Single(
            await service.GetPreferencesAsync(user.Id, cancellationToken));

        Assert.Equal("alternate@example.com", saved.Destination);
        Assert.False(saved.Enabled);
    }

    [Fact]
    public async Task OrganizationSubscriptions_AreIndependentAcrossOrganizations()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        ApplicationUser manager = CreateUser("manager@example.com");
        database.Db.Users.Add(manager);

        Organization first = CreateApprovedOrganization(manager.Id, "First Organization");
        Organization second = CreateApprovedOrganization(manager.Id, "Second Organization");
        database.Db.Organizations.AddRange(first, second);
        await database.Db.SaveChangesAsync(cancellationToken);

        NotificationPreferenceService service = CreatePreferenceService(database.Db);
        IReadOnlyList<OrganizationNotificationSubscriptionView> initial =
            await service.GetOrganizationSubscriptionsAsync(manager.Id, cancellationToken);

        Assert.Equal(2, initial.Count);
        Assert.All(initial, item => Assert.False(item.ReceiveOrganizationNotifications));
        Assert.All(initial, item => Assert.False(item.ReceiveRequestNotifications));
        Assert.All(initial, item => Assert.False(item.ReceiveMembershipNotifications));

        OrganizationNotificationSubscriptionView firstSubscription = Assert.Single(initial, item => item.OrganizationId == first.Id);
        await service.SaveOrganizationSubscriptionAsync(manager.Id, firstSubscription.MembershipId,
            true, true, true, cancellationToken);

        OrganizationNotificationSubscriptionView secondSubscription =
            Assert.Single(initial, item => item.OrganizationId == second.Id);

        await service.SaveOrganizationSubscriptionAsync(
            manager.Id,
            secondSubscription.MembershipId,
            receiveOrganizationNotifications: false,
            receiveRequestNotifications: false,
            receiveMembershipNotifications: true,
            cancellationToken);

        IReadOnlyList<OrganizationNotificationSubscriptionView> updated =
            await service.GetOrganizationSubscriptionsAsync(manager.Id, cancellationToken);

        OrganizationNotificationSubscriptionView firstAfter =
            Assert.Single(updated, item => item.OrganizationId == first.Id);
        OrganizationNotificationSubscriptionView secondAfter =
            Assert.Single(updated, item => item.OrganizationId == second.Id);

        Assert.True(firstAfter.ReceiveOrganizationNotifications);
        Assert.True(firstAfter.ReceiveRequestNotifications);
        Assert.True(firstAfter.ReceiveMembershipNotifications);

        Assert.False(secondAfter.ReceiveOrganizationNotifications);
        Assert.False(secondAfter.ReceiveRequestNotifications);
        Assert.True(secondAfter.ReceiveMembershipNotifications);
    }

    [Fact]
    public async Task RequestEvent_EnqueuesCommonMessaging_ForOwnerAndSubscribedManagerOnly()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        ApplicationUser representative = CreateUser("representative@example.com");
        ApplicationUser manager = CreateUser("manager@example.com");
        ApplicationUser optedOutManager = CreateUser("opted-out-manager@example.com");
        ApplicationUser unrelatedManager = CreateUser("other-manager@example.com");
        database.Db.Users.AddRange(representative, manager, optedOutManager, unrelatedManager);

        Organization organization = CreateApprovedOrganization(manager.Id, "Organization A");
        organization.Memberships.Single(item => item.UserId == manager.Id).SetNotificationSubscriptions(true, true, true);
        OrganizationMembership representativeMembership =
            organization.RequestMembership(Guid.NewGuid(), representative.Id, Now.AddMinutes(1));
        representativeMembership.Approve(manager.Id, Now.AddMinutes(2));

        OrganizationMembership optedOutMembership =
            organization.RequestMembership(Guid.NewGuid(), optedOutManager.Id, Now.AddMinutes(2));
        optedOutMembership.Approve(manager.Id, Now.AddMinutes(3));
        organization.PromoteToAdministrator(optedOutMembership.Id);
        optedOutMembership.SetNotificationSubscriptions(
            receiveOrganizationNotifications: false,
            receiveRequestNotifications: false,
            receiveMembershipNotifications: false);

        Organization unrelatedOrganization =
            CreateApprovedOrganization(unrelatedManager.Id, "Organization B");

        database.Db.Organizations.AddRange(organization, unrelatedOrganization);

        AssistanceRequest request = new(
            Guid.NewGuid(),
            "RP-NOTIFY-001",
            organization.Id,
            representative.Id,
            Now.AddMinutes(3));
        request.SetKind(RequestKind.Project);
        request.SetSummary("Notification integration test");
        request.Submit(representative.Id, Now.AddMinutes(4));
        database.Db.AssistanceRequests.Add(request);

        RequestSubmittedMessage message = new(
            Guid.NewGuid(),
            Now.AddMinutes(4),
            request.Id,
            organization.Id);

        database.Db.OutboxMessages.Add(new OutboxMessage
        {
            Id = message.MessageId,
            MessageType = typeof(RequestSubmittedMessage).FullName ?? nameof(RequestSubmittedMessage),
            Payload = JsonSerializer.Serialize(message),
            OccurredUtc = message.OccurredUtc,
            AttemptCount = 0
        });

        await database.Db.SaveChangesAsync(cancellationToken);

        NotificationPreferenceService preferenceService = CreatePreferenceService(database.Db);
        await preferenceService.EnsureDefaultEmailPreferenceAsync(
            representative.Id,
            representative.Email,
            cancellationToken);
        await preferenceService.EnsureDefaultEmailPreferenceAsync(
            manager.Id,
            manager.Email,
            cancellationToken);
        await preferenceService.EnsureDefaultEmailPreferenceAsync(
            optedOutManager.Id,
            optedOutManager.Email,
            cancellationToken);
        await preferenceService.EnsureDefaultEmailPreferenceAsync(
            unrelatedManager.Id,
            unrelatedManager.Email,
            cancellationToken);

        InMemoryExternalDeliveryQueue queue = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        CommonQueuedNotificationSender sender = new(queue, configuration);

        OutboxDispatcher dispatcher = new(
            database.Db,
            sender,
            new DisabledFfpManagerIntegrationService(),
            Options.Create(new FfpManagerBridgeOptions { Enabled = false }),
            Options.Create(new NotificationOptions
            {
                Enabled = true,
                WhatsAppEnabled = false
            }),
            NullLogger<OutboxDispatcher>.Instance,
            new NullOperationsFlowPublisher(),
            new RequestPortal.Infrastructure.Messages.PortalMessageService(
                database.Db,
                new NullRealtimeNotifier(),
                new DisabledBridgeClient(),
                Options.Create(new FfpManagerBridgeOptions { Enabled = false })));

        int processed = await dispatcher.DispatchBatchAsync(
            batchSize: 10,
            cancellationToken);

        Assert.Equal(1, processed);
        var confirmation = Assert.Single(database.Db.PortalMessages);
        Assert.Contains(request.PublicReference, confirmation.Body);
        Assert.Contains("quote this request code", confirmation.Body);
        Assert.Equal(2, database.Db.MessageRecipients.Count());

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        List<ExternalDeliveryWorkItem> queued = [];
        await foreach (ExternalDeliveryWorkItem item in queue.ReadAllAsync(timeout.Token))
        {
            queued.Add(item);
            if (queued.Count == 2)
            {
                break;
            }
        }

        Assert.Equal(2, queued.Count);
        string[] recipients = queued
            .SelectMany(item => item.Recipients)
            .Select(item => item.Email)
            .Where(email => email is not null)
            .Cast<string>()
            .OrderBy(email => email)
            .ToArray();

        Assert.Equal(
            ["manager@example.com", "representative@example.com"],
            recipients);

        Assert.DoesNotContain(
            queued.SelectMany(item => item.Recipients),
            recipient => recipient.Email == "opted-out-manager@example.com");
        Assert.DoesNotContain(
            queued.SelectMany(item => item.Recipients),
            recipient => recipient.Email == "other-manager@example.com");

        NotificationHistory[] history = database.Db.NotificationHistory
            .Where(item => item.RequestId == request.Id)
            .OrderBy(item => item.Destination)
            .ToArray();

        Assert.Equal(2, history.Length);
        Assert.All(history, item => Assert.Equal(NotificationDeliveryStatus.Sent, item.Status));
        Assert.Equal(
            ["manager@example.com", "representative@example.com"],
            history.Select(item => item.Destination).ToArray());
    }

    [Theory]
    [InlineData("KIN/26/GR/1234", true)]
    [InlineData("RP-26-00123", false)]
    [InlineData(null, false)]
    public async Task QuickIntake_FfpEmailWaitsForFfpReference_AndUsesSeparateOutbox(
        string? applicationNumber, bool hasFfpReference)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(token);
        ApplicationUser applicant = CreateUser("applicant@example.com");
        database.Db.Users.Add(applicant);
        Organization organization = CreateApprovedOrganization(applicant.Id, "Quick Intake - Test Applicant");
        database.Db.Organizations.Add(organization);
        AssistanceRequest request = new(Guid.NewGuid(), "RP-26-00123", organization.Id, applicant.Id, Now);
        request.SetSummary("Food");
        request.Submit(applicant.Id, Now);
        database.Db.AssistanceRequests.Add(request);
        database.Db.QuickIntakes.Add(new QuickIntakeRecord(
            Guid.NewGuid(), request.Id, applicant.Id, organization.Id,
            "Test", "Applicant", new DateOnly(1980, 1, 1), "Male", "Single",
            "+18765550123", null, "1 Test Road", "Kingston", "Kingston",
            "Unemployed", 0, false, false, "None", "Food", Now));
        FfpManagerSubmissionRequestedMessage submission = new(Guid.NewGuid(), Now, request.Id);
        database.Db.OutboxMessages.Add(new OutboxMessage
        {
            Id = submission.MessageId,
            MessageType = typeof(FfpManagerSubmissionRequestedMessage).FullName!,
            Payload = JsonSerializer.Serialize(submission),
            OccurredUtc = Now
        });
        await database.Db.SaveChangesAsync(token);

        RecordingNotificationSender sender = new();
        NotificationOptions options = new() { Enabled = true, FfpEmailAddress = "intake@example.com" };
        OutboxDispatcher dispatcher = new(
            database.Db, sender, new DisabledFfpManagerIntegrationService
            {
                SubmissionReference = new ExternalApplicationReference("1234", applicationNumber, Now)
            },
            Options.Create(new FfpManagerBridgeOptions { Enabled = true }),
            Options.Create(options), NullLogger<OutboxDispatcher>.Instance,
            new NullOperationsFlowPublisher(),
            new RequestPortal.Infrastructure.Messages.PortalMessageService(
                database.Db, new NullRealtimeNotifier(), new DisabledBridgeClient(),
                Options.Create(new FfpManagerBridgeOptions { Enabled = false })));

        int processed = await dispatcher.DispatchBatchAsync(cancellationToken: token);
        Assert.Empty(sender.Notifications);
        if (!hasFfpReference)
        {
            Assert.Equal(0, processed);
            Assert.Null(database.Db.OutboxMessages.Single().ProcessedUtc);
            Assert.Contains("waiting for the reference number", database.Db.OutboxMessages.Single().LastError);
            return;
        }

        Assert.Equal(1, processed);
        OutboxMessage emailOutbox = Assert.Single(database.Db.OutboxMessages,
            item => item.MessageType.EndsWith(nameof(QuickIntakeFfpEmailRequestedMessage)));
        Assert.Null(emailOutbox.ProcessedUtc);

        // Missing mailbox remains retryable; it must not repeat the bridge operation.
        options.FfpEmailAddress = "";
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        Assert.Empty(sender.Notifications);
        Assert.Null(emailOutbox.ProcessedUtc);
        Assert.Contains("Notifications:FfpEmailAddress", emailOutbox.LastError);

        options.FfpEmailAddress = "intake@example.com";
        emailOutbox.AttemptCount = 0;
        emailOutbox.LastError = null;
        await database.Db.SaveChangesAsync(token);
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        NotificationEnvelope email = Assert.Single(sender.Notifications);
        Assert.Equal("intake@example.com", email.Recipient);
        Assert.Equal(NotificationTransport.Email, email.Transport);
        Assert.Contains(applicationNumber!, email.Subject);
        Assert.Contains($"FFP Manager reference number: {applicationNumber}", email.Body);
        Assert.Contains($"Request Portal reference: {request.PublicReference}", email.Body);
        Assert.Contains("Test Applicant", email.Body);
        Assert.Equal(NotificationDeliveryStatus.Sent, Assert.Single(database.Db.NotificationHistory).Status);
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        Assert.Single(sender.Notifications);
    }

    private sealed class RecordingNotificationSender : RequestPortal.Common.Messaging.INotificationSender
    {
        public List<NotificationEnvelope> Notifications { get; } = [];
        public Task SendAsync(NotificationEnvelope notification, CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private static NotificationPreferenceService CreatePreferenceService(
        RequestPortalDbContext dbContext)
    {
        return new NotificationPreferenceService(
            dbContext,
            Options.Create(new NotificationOptions
            {
                Enabled = true,
                WhatsAppEnabled = true
            }),
            new NullBusinessAuditWriter());
    }

    private static ApplicationUser CreateUser(string email)
    {
        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            DisplayName = email,
            CreatedUtc = Now
        };
    }

    private static Organization CreateApprovedOrganization(
        Guid managerUserId,
        string name)
    {
        Organization organization = new(
            Guid.NewGuid(),
            name,
            "JM",
            managerUserId,
            Now);

        organization.RequestMembership(
            Guid.NewGuid(),
            managerUserId,
            Now);

        organization.Approve(
            Guid.NewGuid(),
            Now.AddMinutes(1));

        return organization;
    }

    private sealed class NullBusinessAuditWriter : IBusinessAuditWriter
    {
        public Task WriteAsync(
            Guid actorUserId,
            string action,
            string entityType,
            Guid entityId,
            Guid? organizationId,
            string? detail = null,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NullRealtimeNotifier : RequestPortal.Application.Messages.IPortalMessageRealtimeNotifier
    {
        public Task NotifyAsync(RequestPortal.Application.Messages.PortalMessageCreated message,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NullOperationsFlowPublisher : IRequestPortalOperationsFlowPublisher
    {
        public bool TryEnqueue(
            Guid requestId,
            string stage,
            DateTimeOffset observedAtUtc,
            string? detail = null,
            bool failed = false)
            => true;
    }

    private sealed class DisabledBridgeClient : IExternalApplicationBridgeClient
    {
        public Task<ExternalApplicationReference> SubmitApplicationAsync(
            ExternalApplicationSubmission submission,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("FFP Manager bridge is disabled for this test.");

        public Task UploadArtifactAsync(
            string applicationId,
            ExternalArtifactDescriptor descriptor,
            Stream content,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ExternalApplicationStatus> GetStatusAsync(
            string applicationId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("FFP Manager bridge is disabled for this test.");

        public Task SendInformationResponseAsync(
            ExternalInformationResponse response,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class DisabledFfpManagerIntegrationService : IExternalApplicationIntegrationService
    {
        public ExternalApplicationReference? SubmissionReference { get; init; }
        public Task<ExternalApplicationReference> SubmitRequestAsync(
            Guid requestId,
            CancellationToken cancellationToken = default)
            => SubmissionReference is not null
                ? Task.FromResult(SubmissionReference)
                : throw new InvalidOperationException("FFP Manager is disabled for this test.");

        public Task<ExternalApplicationStatus?> RefreshStatusAsync(
            Guid requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ExternalApplicationStatus?>(null);

        public Task ReceiveStatusAsync(
            Guid messageId,
            Guid requestId,
            ExternalApplicationStatus status,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReceiveInformationRequestAsync(
            ExternalInformationRequest request,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RespondToInformationRequestAsync(
            Guid requestId,
            string applicationId,
            Guid userId,
            string message,
            IReadOnlyList<Guid> documentIds,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
