using System.Text.Json;
using Common.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Portal;
using RequestPortal.Application.Requests;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Time;
using RequestPortal.Domain.ReferenceData;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Requests;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class QuickIntakeClientRelationsEmailTests
{
    private static readonly QuickIntakeSubmission Submission = new(
        "Email", "Verification", new DateOnly(1980, 1, 1), "Male", "Single", "8765550199", null,
        "Test address", "Test community", "Kingston", "Unemployed", 0, false, false, ["None"], "Food");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewIntakeQueuesOneEmailTransactionally_AndRequestRetryDoesNotDuplicate(bool signedIn)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database(token);
        Guid requestId = Guid.NewGuid();
        Guid? applicantId = signedIn ? Guid.NewGuid() : null;
        if (applicantId.HasValue)
        {
            database.Db.Users.Add(new ApplicationUser { Id = applicantId.Value, UserName = "signed-in@example.com",
                NormalizedUserName = "SIGNED-IN@EXAMPLE.COM", DisplayName = "Email Verification", CreatedUtc = DateTimeOffset.UtcNow });
            await database.Db.SaveChangesAsync(token);
        }
        var service = new QuickIntakeService(database.Db);
        var created = await service.CreateAsync(Submission, token, requestId, applicantId);
        var retry = await service.CreateAsync(Submission, token, requestId, applicantId);
        Assert.Equal(created, retry);
        Assert.Equal(1, await database.Db.AssistanceRequests.CountAsync(token));
        var email = await Email(database, requestId, token);
        var payload = JsonSerializer.Deserialize<QuickIntakeClientRelationsEmailRequestedMessage>(email.Payload)!;
        Assert.Equal(created.PublicReference, payload.PublicReference);
        Assert.Equal(requestId, payload.RequestId);
        Assert.Null(payload.FfpManagerReference);
        Assert.Equal(QuickIntakeClientRelationsEmailRequestedMessage.AwaitingReferenceMessageType, email.MessageType);
        Assert.Null(email.ProcessedUtc);
        Assert.Equal(0, email.AttemptCount);
        Assert.Equal(2, await database.Db.OutboxMessages.CountAsync(token));
        Assert.Equal(1, await database.Db.OutboxMessages.CountAsync(m => m.MessageType == typeof(RequestSubmittedMessage).FullName, token));
    }

    [Fact]
    public async Task FailedCreationDoesNotCommitRequestOrEmail()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await PostgreSqlTestDatabase.CreateAsync(token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new QuickIntakeService(database.Db).CreateAsync(Submission, token));
        database.Db.ChangeTracker.Clear();
        Assert.Equal(0, await database.Db.AssistanceRequests.CountAsync(token));
        Assert.Equal(0, await database.Db.OutboxMessages.CountAsync(token));
    }

    [Fact]
    public async Task MissingFfpReferenceWaitsWithoutSendingOrConsumingDeliveryRetries()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database(token);
        var result = await Create(database, token);
        await CompleteOriginalSubmissionEvent(database, token);
        var sender = new CapturingSender();
        var dispatcher = Dispatcher(database, sender);
        for (int i = 0; i < 12; i++) Assert.Equal(0, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        Assert.Empty(sender.Sent);
        var email = await Email(database, result.RequestId, token);
        Assert.Null(email.ProcessedUtc);
        Assert.Equal(0, email.AttemptCount);
        Assert.Null(email.LastError);
    }

    [Fact]
    public async Task IntegrationSuppliesBothReferences_AndDeliveryUsesConfiguredMailbox()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database(token);
        var result = await Create(database, token);
        var bridge = new BridgeClient { Fail = true };
        var integration = Integration(database, bridge);
        await Assert.ThrowsAsync<IntegrationTransferException>(() => integration.SubmitRequestAsync(result.RequestId, token));
        Assert.Null(JsonSerializer.Deserialize<QuickIntakeClientRelationsEmailRequestedMessage>((await Email(database, result.RequestId, token)).Payload)!.FfpManagerReference);
        bridge.Fail = false;
        await integration.SubmitRequestAsync(result.RequestId, token);
        await integration.SubmitRequestAsync(result.RequestId, token);
        await CompleteOriginalSubmissionEvent(database, token);
        var sender = new CapturingSender();
        var dispatcher = Dispatcher(database, sender, new NotificationOptions { Enabled = true, QuickIntakeClientRelationsEmail = "configured@example.com" });
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(cancellationToken: token));
        var sent = Assert.Single(sender.Sent);
        Assert.Equal("configured@example.com", sent.Recipient);
        Assert.Equal($"New Quick Intake Request — {result.PublicReference}", sent.Subject);
        Assert.Contains("A new Quick Intake request has been submitted.", sent.Body);
        Assert.Contains($"Request Portal reference: {result.PublicReference}", sent.Body);
        Assert.Contains("FFP Manager reference: KGN/26/GR/0042", sent.Body);
        Assert.Contains("review this request in Request Portal", sent.Body);
        Assert.Equal(QuickIntakeClientRelationsEmailRequestedMessage.IdForRequest(result.RequestId), sent.DeliveryId);
        Assert.Equal(NotificationTransport.Email, sent.Transport);
        Assert.Equal(3, await database.Db.OutboxMessages.CountAsync(token));
    }

    [Fact]
    public async Task DurableQueueFailureRetriesWithoutLosingRequestOrAddingEmailEvents()
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database(token);
        var result = await Create(database, token);
        await Integration(database, new BridgeClient()).SubmitRequestAsync(result.RequestId, token);
        await CompleteOriginalSubmissionEvent(database, token);
        var sender = new CapturingSender { Fail = true };
        Assert.Equal(0, await Dispatcher(database, sender).DispatchBatchAsync(cancellationToken: token));
        database.Db.ChangeTracker.Clear();
        var email = await Email(database, result.RequestId, token);
        Assert.Null(email.ProcessedUtc);
        Assert.Equal(1, email.AttemptCount);
        Assert.StartsWith("RETRY_AT=", email.LastError);
        Assert.Equal(result, await new QuickIntakeService(database.Db).CreateAsync(Submission, token, result.RequestId));
        email.LastError = $"RETRY_AT={DateTimeOffset.UtcNow.AddMinutes(-1):O}|Simulated durable queue failure";
        email.NextAttemptUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await database.Db.SaveChangesAsync(token);
        sender.Fail = false;
        Assert.Equal(1, await Dispatcher(database, sender).DispatchBatchAsync(cancellationToken: token));
        Assert.Single(sender.Sent);
        Assert.NotNull((await Email(database, result.RequestId, token)).ProcessedUtc);
        Assert.Equal(3, await database.Db.OutboxMessages.CountAsync(token));
        Assert.Equal(1, await database.Db.AssistanceRequests.CountAsync(token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureRetriesDurably_AndCompletedDeliveryIsNotSentAgain(bool graph)
    {
        var token = TestContext.Current.CancellationToken;
        await using var database = await Database(token);
        var result = await Create(database, token);
        await Integration(database, new BridgeClient()).SubmitRequestAsync(result.RequestId, token);
        await CompleteOriginalSubmissionEvent(database, token);
        string path = Path.Combine(Path.GetTempPath(), "quick-intake-email-" + Guid.NewGuid().ToString("N"));
        var options = new ExternalDeliveryOptions { DurableQueuePath = path, InitialRetryDelay = TimeSpan.Zero };
        try
        {
            var queue = new FileExternalDeliveryQueue(options);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["Messaging:MicrosoftGraph:Email:Enabled"] = graph.ToString() }).Build();
            var sender = new CommonQueuedNotificationSender(queue, config);
            Assert.Equal(1, await Dispatcher(database, sender).DispatchBatchAsync(cancellationToken: token));
            var channel = new FailingOnceChannel(graph ? MessageChannel.MsEmail : MessageChannel.Smtp);
            var store = new FileExternalDeliveryIdempotencyStore(options);
            var delivery = new ExternalDeliveryDispatcher([channel], options, idempotencyStore: store);
            // Reopen the disk queue to exercise persistence across worker instances.
            var reopened = new FileExternalDeliveryQueue(options);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            stop.CancelAfter(TimeSpan.FromSeconds(8));
            var processor = new ExternalDeliveryProcessor(reopened, delivery, options);
            Task run = processor.RunAsync(stop.Token);
            await channel.Delivered.Task.WaitAsync(stop.Token);
            Guid id = QuickIntakeClientRelationsEmailRequestedMessage.IdForRequest(result.RequestId);
            string recipient = new NotificationOptions().QuickIntakeClientRelationsEmail;
            await WaitUntil(async () => (await reopened.CheckHealthAsync(token)).Pending == 0, stop.Token);
            Assert.True(await store.HasDeliveredAsync(id, recipient, channel.Channel, token));
            // Simulate replay after a crash between durable handoff and outbox commit.
            var email = await Email(database, result.RequestId, token);
            email.ProcessedUtc = null;
            await database.Db.SaveChangesAsync(token);
            Assert.Equal(1, await Dispatcher(database, sender).DispatchBatchAsync(cancellationToken: token));
            await WaitUntil(async () => (await reopened.CheckHealthAsync(token)).Pending == 0, stop.Token);
            Assert.Equal(2, channel.Attempts);
            Assert.Equal(1, channel.Successes);
            Assert.Equal(1, await database.Db.AssistanceRequests.CountAsync(token));
            stop.Cancel();
            try { await run; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, CancellationToken token)
    { while (!await condition()) await Task.Delay(20, token); }

    private static async Task<PostgreSqlTestDatabase> Database(CancellationToken token)
    {
        var db = await PostgreSqlTestDatabase.CreateAsync(token);
        db.Db.UnitsOfMeasure.Add(new UnitOfMeasure(Guid.NewGuid(), "EA", "Each"));
        await db.Db.SaveChangesAsync(token);
        return db;
    }
    private static Task<QuickIntakeResult> Create(PostgreSqlTestDatabase db, CancellationToken token) =>
        new QuickIntakeService(db.Db).CreateAsync(Submission, token, Guid.NewGuid());
    private static Task<RequestPortal.Infrastructure.Persistence.OutboxMessage> Email(PostgreSqlTestDatabase db, Guid requestId, CancellationToken token) =>
        db.Db.OutboxMessages.SingleAsync(m => m.Id == QuickIntakeClientRelationsEmailRequestedMessage.IdForRequest(requestId), token);
    private static async Task CompleteOriginalSubmissionEvent(PostgreSqlTestDatabase db, CancellationToken token)
    {
        var original = await db.Db.OutboxMessages.SingleAsync(m => m.MessageType == typeof(RequestSubmittedMessage).FullName, token);
        original.ProcessedUtc = DateTimeOffset.UtcNow;
        // These tests isolate staff delivery; applicant delivery is covered separately.
        var confirmation = await db.Db.OutboxMessages.SingleOrDefaultAsync(
            m => m.MessageType == typeof(FfpManagerClientConfirmationRequestedMessage).FullName, token);
        if (confirmation is not null) confirmation.ProcessedUtc = DateTimeOffset.UtcNow;
        await db.Db.SaveChangesAsync(token);
    }
    private static FfpManagerIntegrationService Integration(PostgreSqlTestDatabase db, BridgeClient bridge) =>
        new(db.Db, null!, bridge, Options.Create(new FfpManagerBridgeOptions { Enabled = true }), new SystemClock(), null!);
    private static OutboxDispatcher Dispatcher(PostgreSqlTestDatabase db, RequestPortal.Common.Messaging.INotificationSender sender, NotificationOptions? options = null) =>
        new(db.Db, sender, null!, Options.Create(new FfpManagerBridgeOptions { Enabled = true }),
            Options.Create(options ?? new NotificationOptions { Enabled = true }), NullLogger<OutboxDispatcher>.Instance, new Flow(), null!);

    private sealed class CapturingSender : RequestPortal.Common.Messaging.INotificationSender
    {
        public bool Fail { get; set; }
        public List<NotificationEnvelope> Sent { get; } = [];
        public Task SendAsync(NotificationEnvelope notification, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("Simulated durable queue failure");
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }
    private sealed class FailingOnceChannel(MessageChannel channel) : IExternalMessageChannel
    {
        public MessageChannel Channel => channel;
        public int Attempts { get; private set; }
        public int Successes { get; private set; }
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendAsync(MessageRecipient recipient, MessageRequest request, Guid notificationId, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts == 1) throw new IOException("Simulated transport outage");
            Successes++;
            Delivered.TrySetResult();
            return Task.CompletedTask;
        }
    }
    private sealed class Flow : IRequestPortalOperationsFlowPublisher
    { public bool TryEnqueue(Guid requestId, string stage, DateTimeOffset observedAtUtc, string? detail = null, bool failed = false) => true; }
    private sealed class BridgeClient : IExternalApplicationBridgeClient
    {
        public bool Fail { get; set; }
        private ExternalApplicationReceipt? receipt;
        public Task<ExternalApplicationReceipt?> FindApplicationAsync(Guid requestId, CancellationToken cancellationToken = default) => Task.FromResult(receipt);
        public Task<ExternalApplicationReference> SubmitApplicationAsync(ExternalApplicationSubmission submission, CancellationToken cancellationToken = default)
        {
            if(Fail) throw new HttpRequestException("Simulated bridge outage");
            receipt = new(submission.PortalRequestId,"123","KGN/26/GR/0042",submission.SubmittedUtc,"JMDB01","FFPJM");
            return Task.FromResult(new ExternalApplicationReference("123", "KGN/26/GR/0042", submission.SubmittedUtc));
        }
        public Task UploadArtifactAsync(string applicationId, ExternalArtifactDescriptor descriptor, Stream content, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ExternalApplicationStatus> GetStatusAsync(string applicationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendInformationResponseAsync(ExternalInformationResponse response, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
