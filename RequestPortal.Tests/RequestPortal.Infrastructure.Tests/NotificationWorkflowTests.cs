using Microsoft.EntityFrameworkCore;
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
    [InlineData("intake@example.com", "account@example.com", true, false, 2)]
    [InlineData("intake@example.com", null, true, false, 1)]
    [InlineData("intake@example.com", null, false, false, 1)]
    [InlineData("ACCOUNT@example.com", "account@example.com", true, false, 1)]
    [InlineData(null, "account@example.com", true, false, 1)]
    [InlineData(null, null, false, false, 0)]
    [InlineData("intake@example.com", "account@example.com", true, true, 0)]
    public async Task QuickIntakeApplicantEmailUsesExistingDeliveryWithoutWhatsAppOrHistoricalReplay(
        string? intakeEmail, string? accountEmail, bool signedIn, bool optedOut, int expectedEmails)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        database.Db.UnitsOfMeasure.Add(new RequestPortal.Domain.ReferenceData.UnitOfMeasure(Guid.NewGuid(), "EA", "Each"));
        Guid? applicantId = signedIn ? Guid.NewGuid() : null;
        if (applicantId.HasValue)
            database.Db.Users.Add(new ApplicationUser { Id = applicantId.Value, UserName = "fixture@example.com",
                Email = accountEmail, DisplayName = "Fixture applicant", CreatedUtc = Now });
        await database.Db.SaveChangesAsync(token);
        var created = await new RequestPortal.Infrastructure.Requests.QuickIntakeService(database.Db).CreateAsync(
            new RequestPortal.Application.Requests.QuickIntakeSubmission("Test", "Applicant", new DateOnly(1980, 1, 1),
                "Male", "Single", "8765550123", intakeEmail, "Test road", "Test community", "Kingston",
                "Unemployed", 0, false, false, ["None"], "Food"), token, applicantUserId: applicantId);
        var request = await database.Db.AssistanceRequests.SingleAsync(token);
        if (optedOut)
            database.Db.NotificationPreferences.Add(new(Guid.NewGuid(), request.CreatedByUserId,
                NotificationChannel.Email, accountEmail!, enabled: false));
        // Isolated fixtures stand in for already-processed historical events; never replay them.
        var historical = await database.Db.OutboxMessages.ToArrayAsync(token);
        foreach (var item in historical) item.ProcessedUtc = Now;
        var message = new FfpManagerClientConfirmationRequestedMessage(Guid.NewGuid(), Now, created.RequestId, "FIXTURE/26/0042");
        database.Db.OutboxMessages.Add(new OutboxMessage { Id = message.MessageId, OccurredUtc = message.OccurredUtc,
            MessageType = typeof(FfpManagerClientConfirmationRequestedMessage).FullName!, Payload = JsonSerializer.Serialize(message) });
        await database.Db.SaveChangesAsync(token);
        var queue = new ApplicantEmailQueue();
        var sender = new CommonQueuedNotificationSender(queue, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Messaging:MicrosoftGraph:Email:Enabled"] = "true" }).Build());
        var dispatcher = new OutboxDispatcher(database.Db, sender, new DisabledFfpManagerIntegrationService(),
            Options.Create(new FfpManagerBridgeOptions { Enabled = false }),
            Options.Create(new NotificationOptions { Enabled = true, WhatsAppEnabled = false }),
            NullLogger<OutboxDispatcher>.Instance, new NullOperationsFlowPublisher(),
            new RequestPortal.Infrastructure.Messages.PortalMessageService(database.Db, new NullRealtimeNotifier(),
                new DisabledBridgeClient(), Options.Create(new FfpManagerBridgeOptions { Enabled = false })));
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        Assert.Equal(expectedEmails, queue.Items.Count);
        Assert.All(queue.Items, item => {
            Assert.Equal(MessageChannel.MsEmail, item.RequestedChannels);
            Assert.Contains("FIXTURE/26/0042", item.Request.Body);
        });
        var addresses = queue.Items.SelectMany(item => item.Recipients).Select(recipient => recipient.Email).ToArray();
        if (expectedEmails > 0 && intakeEmail is not null)
            Assert.Contains(addresses, address => string.Equals(address, intakeEmail, StringComparison.OrdinalIgnoreCase));
        if (signedIn && accountEmail is not null && !optedOut) Assert.Contains(accountEmail, addresses);
        Assert.Equal(queue.Items.Count, queue.Items.Select(item => item.NotificationId).Distinct().Count());
        Assert.DoesNotContain(database.Db.NotificationHistory, history => history.Channel == NotificationChannel.WhatsApp);
        Assert.All(historical, item => Assert.Equal(Now, item.ProcessedUtc));
    }

    private sealed class ApplicantEmailQueue : IExternalDeliveryQueue
    {
        public List<ExternalDeliveryWorkItem> Items { get; } = [];
        public ValueTask EnqueueAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default)
        {
            Assert.Equal(MessageChannel.MsEmail, item.RequestedChannels);
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
        public IAsyncEnumerable<ExternalDeliveryWorkItem> ReadAllAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This fixture records queued email without submitting deliveries.");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ReferenceConfirmationUsesConsentedChannels_IndependentlyOfStaffEmailFailure(bool whatsApp, bool consent)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        database.Db.UnitsOfMeasure.Add(new RequestPortal.Domain.ReferenceData.UnitOfMeasure(Guid.NewGuid(), "EA", "Each"));
        await database.Db.SaveChangesAsync(token);
        var created = await new RequestPortal.Infrastructure.Requests.QuickIntakeService(database.Db).CreateAsync(
            new RequestPortal.Application.Requests.QuickIntakeSubmission("Test", "Applicant", new DateOnly(1980,1,1),
                "Male", "Single", "8765550123", "client@example.com", "Test road", "Test community", "Kingston",
                "Unemployed", 0, false, false, ["None"], "Food"), token);
        var request = await database.Db.AssistanceRequests.SingleAsync(token);
        var applicant = await database.Db.Users.SingleAsync(token);
        var whatsAppPreference = await database.Db.NotificationPreferences.SingleAsync(
            item => item.UserId == applicant.Id && item.Channel == NotificationChannel.WhatsApp, token);
        whatsAppPreference.Update("+18765550123", consent);
        var original = await database.Db.OutboxMessages.SingleAsync(m => m.MessageType == typeof(RequestSubmittedMessage).FullName, token);
        original.ProcessedUtc = DateTimeOffset.UtcNow;
        var staff = await database.Db.OutboxMessages.SingleAsync(m => m.Id == QuickIntakeClientRelationsEmailRequestedMessage.IdForRequest(request.Id), token);
        staff.Payload = JsonSerializer.Serialize(new QuickIntakeClientRelationsEmailRequestedMessage(
            staff.Id, staff.OccurredUtc, request.Id, request.PublicReference, "KGN/26/GR/0042"));
        staff.MessageType = typeof(QuickIntakeClientRelationsEmailRequestedMessage).FullName!;
        var client = new FfpManagerClientConfirmationRequestedMessage(
            FfpManagerClientConfirmationRequestedMessage.IdForRequest(request.Id), Now, request.Id, "KGN/26/GR/0042");
        database.Db.OutboxMessages.Add(new OutboxMessage { Id=client.MessageId, OccurredUtc=client.OccurredUtc,
            MessageType=typeof(FfpManagerClientConfirmationRequestedMessage).FullName!, Payload=JsonSerializer.Serialize(client) });
        await database.Db.SaveChangesAsync(token);
        var sender = new RecordingReferenceSender();
        var options = new NotificationOptions { Enabled=true, WhatsAppEnabled=whatsApp, QuickIntakeClientRelationsEmail="" };
        var dispatcher = new OutboxDispatcher(database.Db, sender, new DisabledFfpManagerIntegrationService(),
            Options.Create(new FfpManagerBridgeOptions { Enabled=true }), Options.Create(options),
            NullLogger<OutboxDispatcher>.Instance, new NullOperationsFlowPublisher(),
            new RequestPortal.Infrastructure.Messages.PortalMessageService(database.Db, new NullRealtimeNotifier(),
                new DisabledBridgeClient(), Options.Create(new FfpManagerBridgeOptions { Enabled=false })));
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        Assert.NotNull(database.Db.OutboxMessages.Single(m=>m.Id==client.MessageId).ProcessedUtc);
        Assert.Null(staff.ProcessedUtc);
        var email = Assert.Single(sender.Notifications, m=>m.Transport==NotificationTransport.Email);
        Assert.Equal("client@example.com", email.Recipient);
        Assert.Contains("KGN/26/GR/0042", email.Body);
        Assert.Contains(request.PublicReference, email.Body);
        Assert.NotNull(email.DeliveryId);
        Assert.Equal(whatsApp && consent ? 1 : 0, sender.Notifications.Count(m=>m.Transport==NotificationTransport.WhatsApp));
        if (whatsApp && consent)
            Assert.Equal("request_portal_request_update", Assert.Single(sender.Notifications, m=>m.Transport==NotificationTransport.WhatsApp).WhatsAppTemplateName);
        options.QuickIntakeClientRelationsEmail="clientrelations@example.com";
        staff.AttemptCount=0; staff.LastError=null; staff.NextAttemptUtc=null; staff.IsTerminal=false;
        await database.Db.SaveChangesAsync(token);
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        Assert.Single(sender.Notifications, m=>m.Recipient=="clientrelations@example.com");
        // Replaying a handoff retains stable delivery identities for provider deduplication.
        var before = sender.Notifications.Where(m=>m.Recipient!="clientrelations@example.com").Select(m=>m.DeliveryId).ToArray();
        database.Db.OutboxMessages.Single(m=>m.Id==client.MessageId).ProcessedUtc=null;
        await database.Db.SaveChangesAsync(token);
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        Assert.Equal(before, sender.Notifications.Where(m=>m.Recipient!="clientrelations@example.com").Skip(before.Length).Select(m=>m.DeliveryId).ToArray());
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(cancellationToken: token));
    }

    [Fact]
    public async Task RequestSubmitted_WhenBridgeEnabled_DoesNotSendPrematureConfirmation()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(token);
        RequestSubmittedMessage message = new(Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid());
        database.Db.OutboxMessages.Add(new OutboxMessage
        {
            Id = message.MessageId, OccurredUtc = message.OccurredUtc,
            MessageType = typeof(RequestSubmittedMessage).FullName!, Payload = JsonSerializer.Serialize(message)
        });
        await database.Db.SaveChangesAsync(token);
        RecordingReferenceSender sender = new();
        OutboxDispatcher dispatcher = new(database.Db, sender, new DisabledFfpManagerIntegrationService(),
            Options.Create(new FfpManagerBridgeOptions { Enabled = true }),
            Options.Create(new NotificationOptions { Enabled = true, WhatsAppEnabled = true }),
            NullLogger<OutboxDispatcher>.Instance, new NullOperationsFlowPublisher(),
            new RequestPortal.Infrastructure.Messages.PortalMessageService(database.Db, new NullRealtimeNotifier(),
                new DisabledBridgeClient(), Options.Create(new FfpManagerBridgeOptions { Enabled = false })));
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        Assert.Empty(sender.Notifications);
        Assert.Single(database.Db.OutboxMessages,
            item => item.MessageType.EndsWith(nameof(FfpManagerSubmissionRequestedMessage)));
    }

    private sealed class RecordingReferenceSender : RequestPortal.Common.Messaging.INotificationSender
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
        public Task<ExternalApplicationReference> SubmitRequestAsync(
            Guid requestId,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("FFP Manager is disabled for this test.");

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
