using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Common.Messaging;
using FfpManager.Bridge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Messages;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Notifications;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Messages;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Requests;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class FfpManagerStatusMappingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly RequestStatus[] Mapped = [RequestStatus.Submitted, RequestStatus.MoreInformationRequired,
        RequestStatus.Approved, RequestStatus.Rejected, RequestStatus.Cancelled, RequestStatus.Fulfilled,
        RequestStatus.OnHold, RequestStatus.Investigated, RequestStatus.SentForAssessment];

    [Theory]
    [InlineData("Pending Investigation", RequestStatus.Submitted)]
    [InlineData("More Information Required", RequestStatus.MoreInformationRequired)]
    [InlineData("Incomplete", RequestStatus.MoreInformationRequired)]
    [InlineData("Approved", RequestStatus.Approved)]
    [InlineData("Unable to Assist", RequestStatus.Rejected)]
    [InlineData("No Longer In Need", RequestStatus.Cancelled)]
    [InlineData("Complete", RequestStatus.Fulfilled)]
    [InlineData("On Hold", RequestStatus.OnHold)]
    [InlineData("Investigated", RequestStatus.Investigated)]
    [InlineData("Sent for Assessment", RequestStatus.SentForAssessment)]
    public void ExactCodesAndOnlyCaseOrOuterWhitespaceNormalization(string code, RequestStatus expected)
    {
        Assert.Equal(expected, FfpManagerStatusMapping.Map(code));
        Assert.Equal(expected, FfpManagerStatusMapping.Map(" \t" + code.ToLowerInvariant() + "\r\n"));
        Assert.Null(FfpManagerStatusMapping.Map(code + " extra"));
    }

    [Theory]
    [InlineData(null)][InlineData("")][InlineData(" \t\r\n")][InlineData("RECEIVED")]
    [InlineData("Closed")][InlineData("PendingFulfilment")][InlineData("REJECTED")]
    [InlineData("More_Information_Required")][InlineData("On-Hold")][InlineData("Sent  for Assessment")]
    [InlineData("1")][InlineData("Unknown")]
    public void UnmappedCodesAreNotGuessed(string? code) => Assert.Null(FfpManagerStatusMapping.Map(code));

    [Fact]
    public void ExistingEnumNumbersArePreservedAndNewLabelsAreReadable()
    {
        string[] existing = ["Draft", "Submitted", "MoreInformationRequired", "Approved", "Rejected", "Cancelled",
            "PendingFulfilment", "Fulfilled", "Closed", "Duplicate", "RepresentativeReviewRequired"];
        for (int i = 0; i < existing.Length; i++) Assert.Equal(i, (int)Enum.Parse<RequestStatus>(existing[i]));
        Assert.Equal(11, (int)RequestStatus.OnHold); Assert.Equal(12, (int)RequestStatus.Investigated);
        Assert.Equal(13, (int)RequestStatus.SentForAssessment);
        Assert.Equal("On Hold", RequestStatusPresentation.Label("OnHold"));
        Assert.Equal("Investigated", RequestStatusPresentation.Label("Investigated"));
        Assert.Equal("Sent for Assessment", RequestStatusPresentation.Label("SentForAssessment"));
    }

    [Fact]
    public void MappedStatesCanMoveDirectlyAmongAssessmentHoldApprovalAndOutcomes()
    {
        foreach (var from in Mapped)
        foreach (var to in Mapped)
        {
            var request = Submitted();
            request.ApplyFfpManagerStatus(from, Guid.NewGuid(), Now, null);
            int before = request.StatusHistory.Count;
            Assert.Equal(from != to, request.ApplyFfpManagerStatus(to, Guid.NewGuid(), Now.AddMinutes(1), "Detail"));
            Assert.Equal(to, request.Status);
            Assert.Equal(before + (from == to ? 0 : 1), request.StatusHistory.Count);
        }
        var direct = Submitted();
        Assert.True(direct.ApplyFfpManagerStatus(FfpManagerStatusMapping.Map("Complete")!.Value, Guid.NewGuid(), Now, null));
        Assert.Equal(RequestStatus.Fulfilled, direct.Status);
        Assert.Equal(3, direct.StatusHistory.Count); // Draft, Submitted, Fulfilled; no invented steps.
        var pending = Submitted(); pending.Approve(Guid.NewGuid(), Now); pending.MarkPendingFulfilment(Guid.NewGuid(), Now);
        Assert.True(pending.ApplyFfpManagerStatus(RequestStatus.Fulfilled, Guid.NewGuid(), Now, null));
    }

    [Fact]
    public void PortalSpecificProtectionsAreRetained()
    {
        var draft = new AssistanceRequest(Guid.NewGuid(), "FIXTURE", Guid.NewGuid(), Guid.NewGuid(), Now);
        var duplicate = Submitted(); duplicate.MarkDuplicate(Guid.NewGuid(), Now, "Duplicate");
        var review = Submitted(); review.HoldForRepresentativeReview(Guid.NewGuid(), Now, "Review");
        var closed = Submitted(); closed.ApplyFfpManagerStatus(RequestStatus.Fulfilled, Guid.NewGuid(), Now, null); closed.Close(Guid.NewGuid(), Now);
        foreach (var request in new[] { draft, duplicate, review, closed })
        {
            var status = request.Status; int count = request.StatusHistory.Count;
            foreach (var mapped in Mapped) Assert.False(request.ApplyFfpManagerStatus(mapped, Guid.NewGuid(), Now, null));
            Assert.Equal(status, request.Status); Assert.Equal(count, request.StatusHistory.Count);
        }
    }

    [Fact]
    public async Task PollsCallbacksAndInformationRequestsShareTransitionIdentityAndAuditOriginalCode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture(ct);
        var db = fixture.Db; var request = await db.AssistanceRequests.SingleAsync(ct);
        var logger = new CapturingLog(); var service = Service(db, logger);
        Guid callback = Guid.NewGuid();
        var incomplete = new ExternalApplicationStatus("42", " Incomplete ", "Not a mapping input", Now, "Supply proof.");
        await service.ReceiveStatusAsync(callback, request.Id, incomplete, ct);
        await service.ReceiveStatusAsync(callback, request.Id, incomplete, ct);
        await service.ReceiveStatusAsync(Guid.NewGuid(), request.Id, incomplete with { StatusCode = "More Information Required" }, ct);
        await service.ReceiveInformationRequestAsync(new(Guid.NewGuid(), request.Id, "42", "Supply proof and dates.", Now), ct);
        var first = Assert.Single(StatusEvents(db));
        Assert.Contains("Supply proof and dates.", JsonSerializer.Deserialize<RequestStatusChangedMessage>(first.Payload)!.Detail);
        var audit = Assert.Single(db.AuditEntries, item => item.Action == "FfpManagerStatusChanged");
        using (var detail = JsonDocument.Parse(audit.Detail!))
        {
            Assert.Equal(" Incomplete ", detail.RootElement.GetProperty("SourceCode").GetString());
            Assert.Equal("Submitted", detail.RootElement.GetProperty("PreviousStatus").GetString());
            Assert.Equal("MoreInformationRequired", detail.RootElement.GetProperty("NewStatus").GetString());
            Assert.Equal("FFPManager", detail.RootElement.GetProperty("SourceSystem").GetString());
        }
        await service.ReceiveStatusAsync(Guid.NewGuid(), request.Id, incomplete with { StatusCode = "Pending Investigation", ChangedUtc = Now.AddMinutes(1) }, ct);
        await service.ReceiveStatusAsync(Guid.NewGuid(), request.Id, incomplete with { ChangedUtc = Now.AddMinutes(2) }, ct);
        Assert.Equal(3, StatusEvents(db).Count());
        Assert.Equal(3, StatusEvents(db).Select(item => item.Id).Distinct().Count());
        await service.ReceiveStatusAsync(callback, request.Id, incomplete, ct); // Old callback remains deduplicated after a real return.
        foreach (string? code in new string?[] { null, "", "  ", "Unknown\n" + new string('x', 500) })
            await service.ReceiveStatusAsync(Guid.NewGuid(), request.Id, incomplete with { StatusCode = code! }, ct);
        Assert.Equal(3, StatusEvents(db).Count());
        Assert.Equal(RequestStatus.MoreInformationRequired, request.Status);
        Assert.Single(logger.Messages); Assert.True(logger.Messages[0].Length < 220);
        Assert.DoesNotContain('\n', logger.Messages[0]);
    }

    [Fact]
    public async Task PolledCodeAndCallbackCodeUseTheSameTransitionAndDoNotDuplicateNotifications()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture(ct); var db = fixture.Db;
        var request = await db.AssistanceRequests.SingleAsync(ct);
        var link = await db.IntegrationLinks.SingleAsync(ct);
        link.MarkSynchronized("42", Now, "FFP/26/0042"); await db.SaveChangesAsync(ct);
        var status = new ExternalApplicationStatus("42", " Complete ", "Approved", Now);
        var bridge = MessageNavigationPageTests.Proxy.For<IExternalApplicationBridgeClient>((method, _) => method switch {
            "FindApplicationAsync" => Task.FromResult<ExternalApplicationReceipt?>(new(request.Id, "42", "FFP/26/0042", Now, "JMDB01", "FFPJM")),
            "GetStatusAsync" => Task.FromResult(status),
            _ => throw new NotSupportedException(method)
        });
        var service = new FfpManagerIntegrationService(db, null!, bridge,
            Options.Create(new FfpManagerBridgeOptions { Enabled = true }), new Clock(), null!);
        var polled = await service.RefreshStatusAsync(request.Id, ct);
        Assert.Equal(" Complete ", polled!.StatusCode);
        await service.ReceiveStatusAsync(Guid.NewGuid(), request.Id, polled, ct);
        await service.ReceiveStatusAsync(Guid.NewGuid(), request.Id, status, ct);
        Assert.Equal(RequestStatus.Fulfilled, request.Status);
        Assert.Single(StatusEvents(db));
        Assert.Single(db.AuditEntries, item => item.Action == "FfpManagerStatusChanged");
    }

    [Fact]
    public async Task InformationRequestFirstAndStatusFirstEachProduceOneLogicalNotification()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (bool infoFirst in new[] { true, false })
        {
            await using var fixture = await Fixture(ct); var db = fixture.Db;
            var request = await db.AssistanceRequests.SingleAsync(ct); var service = Service(db);
            var info = new ExternalInformationRequest(Guid.NewGuid(), request.Id, "42", "Provide a dated document.", Now);
            var status = new ExternalApplicationStatus("42", "More Information Required", "Ignored", Now);
            if (infoFirst) await service.ReceiveInformationRequestAsync(info, ct);
            await service.ReceiveStatusAsync(Guid.NewGuid(), request.Id, status, ct);
            await service.ReceiveInformationRequestAsync(info, ct);
            await service.ReceiveInformationRequestAsync(info with { MessageId = Guid.NewGuid() }, ct);
            Assert.Single(StatusEvents(db));
            Assert.DoesNotContain(db.OutboxMessages, item => item.MessageType == typeof(FfpManagerInformationRequestedMessage).FullName);
        }
    }

    [Theory]
    [InlineData("applicant@example.com", false)]
    [InlineData("applicant@example.com", true)]
    [InlineData(null, true)]
    public async Task AssessmentUsesApplicantEmailAndExactInternalMailboxWithNoMobileDelivery(string? email, bool staffEntered)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture(ct, email, staffEntered); var db = fixture.Db;
        var request = await db.AssistanceRequests.SingleAsync(ct); var service = Service(db);
        var link = await db.IntegrationLinks.SingleAsync(ct);
        link.MarkSynchronized("42", Now, "FFP/26/0042"); await db.SaveChangesAsync(ct);
        await service.ReceiveStatusAsync(Guid.NewGuid(), request.Id, new("42", "Sent for Assessment", "Ignored", Now), ct);
        var queue = new Queue(); var dispatcher = Dispatcher(db, queue);
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(cancellationToken: ct));
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(cancellationToken: ct));
        var addresses = queue.Items.SelectMany(item => item.Recipients).Select(item => item.Email).ToArray();
        if (email is not null) Assert.Contains(email, addresses);
        else Assert.Contains(db.NotificationHistory, item => item.Status == NotificationDeliveryStatus.Skipped && item.LastError!.Contains("No email destination"));
        Assert.DoesNotContain("intake-staff@example.com", addresses);
        Assert.DoesNotContain("clientrelations@foodforthepoorja.org", addresses);
        var assessment = Assert.Single(queue.Items, item => item.Recipients.Single().Email == "assessment@ffpja.org");
        Assert.Contains(request.PublicReference, assessment.Request.Body); Assert.Contains("FFP/26/0042", assessment.Request.Body);
        Assert.Contains("/Staff/Review/" + request.Id, assessment.Request.Body);
        Assert.All(queue.Items, item => Assert.Equal(MessageChannel.MsEmail, item.RequestedChannels));
        var portal = Assert.Single(db.PortalMessages); Assert.Contains("Sent for Assessment", portal.Body);
        Assert.Contains(request.PublicReference, portal.Body);
        Assert.DoesNotContain(db.NotificationHistory, item => item.Channel == NotificationChannel.WhatsApp);
    }

    [Fact]
    public async Task EmailFailureRetainsStatusAuditAndRetryableOutboxWithStableDeliveryIdentities()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture(ct); var db = fixture.Db;
        var request = await db.AssistanceRequests.SingleAsync(ct);
        await Service(db).ReceiveStatusAsync(Guid.NewGuid(), request.Id, new("42", "Sent for Assessment", "Ignored", Now), ct);
        var queue = new Queue { FailAssessmentOnce = true }; var dispatcher = Dispatcher(db, queue);
        Assert.Equal(0, await dispatcher.DispatchBatchAsync(cancellationToken: ct));
        Assert.Equal(RequestStatus.SentForAssessment, request.Status);
        Assert.Single(db.AuditEntries, item => item.Action == "FfpManagerStatusChanged");
        var pending = Assert.Single(StatusEvents(db)); Assert.Null(pending.ProcessedUtc); Assert.Equal(1, pending.AttemptCount);
        Assert.NotNull(pending.LastError); Assert.False(pending.IsTerminal);
        pending.NextAttemptUtc = null; pending.LastError = null; pending.AttemptCount = 0; await db.SaveChangesAsync(ct);
        Assert.Equal(1, await dispatcher.DispatchBatchAsync(cancellationToken: ct));
        Assert.Single(db.PortalMessages); Assert.Equal(2, queue.Items.Count); // Queue deduplicates the applicant handoff on retry.
        Assert.All(queue.Items, item => Assert.Equal(MessageChannel.MsEmail, item.RequestedChannels));
    }

    [Theory]
    [InlineData("")][InlineData(" Incomplete ")][InlineData("On Hold")]
    public async Task CallbackReadsAuthoritativeCodeInsteadOfLegacyNameFallback(string code)
    {
        var ct = TestContext.Current.CancellationToken;
        var gateway = new Gateway(new("42", code, "Approved", Now)); var http = new CallbackHttp();
        var dispatcher = new IntegrationOutboxDispatcher(null!, http, Options.Create(new BridgeOptions {
            StatusMap = new(StringComparer.OrdinalIgnoreCase) { ["Approved"] = "APPROVED" } }),
            NullLogger<IntegrationOutboxDispatcher>.Instance, gateway);
        var stale = new IntegrationOutboxMessage(1, Guid.NewGuid(), "ApplicationStatusChanged", "42", Guid.NewGuid(),
            "Approved", "Approved", null, null, null, null, Now, 0);
        var method = typeof(IntegrationOutboxDispatcher).GetMethod("PostStatusAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(dispatcher, [stale, new BridgeOptions(), ct])!;
        Assert.Equal(code, http.Callback!.Status.StatusCode);
        Assert.Equal(stale.EventId, http.Callback.MessageId);
        Assert.Equal(Now, http.Callback.Status.ChangedUtc);
    }

    private static AssistanceRequest Submitted()
    {
        var request = new AssistanceRequest(Guid.NewGuid(), "FIXTURE", Guid.NewGuid(), Guid.NewGuid(), Now);
        request.SetKind(RequestKind.Project); request.Submit(request.CreatedByUserId, Now); return request;
    }
    private static IQueryable<OutboxMessage> StatusEvents(RequestPortalDbContext db) =>
        db.OutboxMessages.Where(item => item.MessageType == typeof(RequestStatusChangedMessage).FullName);
    private static FfpManagerIntegrationService Service(RequestPortalDbContext db, CapturingLog? logger = null) =>
        new(db, null!, null!, Options.Create(new FfpManagerBridgeOptions()), new Clock(), null!, logger);
    private static OutboxDispatcher Dispatcher(RequestPortalDbContext db, Queue queue)
    {
        var sender = new CommonQueuedNotificationSender(queue, new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Messaging:MicrosoftGraph:Email:Enabled"] = "true" }).Build());
        return new(db, sender, null!, Options.Create(new FfpManagerBridgeOptions()),
            Options.Create(new NotificationOptions { Enabled = true, WhatsAppEnabled = false }), NullLogger<OutboxDispatcher>.Instance,
            null!, new PortalMessageService(db, new Realtime(), null!, Options.Create(new FfpManagerBridgeOptions())));
    }
    private static async Task<PostgreSqlTestDatabase> Fixture(CancellationToken ct, string? email = "applicant@example.com", bool staff = false)
    {
        var fixture = await PostgreSqlTestDatabase.CreateAsync(ct); var db = fixture.Db;
        db.UnitsOfMeasure.Add(new RequestPortal.Domain.ReferenceData.UnitOfMeasure(Guid.NewGuid(), "EA", "Each"));
        Guid? userId = staff ? Guid.NewGuid() : null;
        if (userId.HasValue) db.Users.Add(new ApplicationUser { Id = userId.Value, UserName = "intake-staff@example.com",
            Email = "intake-staff@example.com", IsInternalStaff = true, CreatedUtc = Now });
        await db.SaveChangesAsync(ct);
        await new QuickIntakeService(db).CreateAsync(new("Test", "Applicant", new(1980, 1, 1), "Male", "Single", "8765550123",
            email, "Test road", "Test district", "Kingston", "Unemployed", 0, false, false, ["None"], "Food"), ct, applicantUserId: userId);
        foreach (var item in await db.OutboxMessages.ToArrayAsync(ct)) item.ProcessedUtc = Now;
        await db.SaveChangesAsync(ct); return fixture;
    }
    private sealed record Clock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class Realtime : IPortalMessageRealtimeNotifier
    { public Task NotifyAsync(PortalMessageCreated message, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class Queue : IExternalDeliveryQueue
    {
        public List<ExternalDeliveryWorkItem> Items { get; } = [];
        public bool FailAssessmentOnce { get; set; }
        public ValueTask EnqueueAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default)
        {
            Assert.Equal(MessageChannel.MsEmail, item.RequestedChannels);
            if (FailAssessmentOnce && item.Recipients.Single().Email == "assessment@ffpja.org")
            { FailAssessmentOnce = false; throw new IOException("Mock queue unavailable"); }
            if (Items.All(existing => existing.NotificationId != item.NotificationId)) Items.Add(item);
            return ValueTask.CompletedTask;
        }
        public IAsyncEnumerable<ExternalDeliveryWorkItem> ReadAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class CapturingLog : ILogger<FfpManagerIntegrationService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
    private sealed class Gateway(ExternalApplicationStatus status) : IFfpManagerGateway
    {
        public Task<ExternalApplicationStatus?> GetStatusAsync(string applicationId, CancellationToken cancellationToken = default) => Task.FromResult<ExternalApplicationStatus?>(status);
        public Task<ExternalApplicationReceipt?> FindApplicationAsync(Guid requestId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalApplicationReference> CreateApplicationAsync(ExternalApplicationSubmission submission, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveArtifactAsync(string applicationId, ExternalArtifactDescriptor descriptor, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveInformationResponseAsync(ExternalInformationResponse response, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalApplicationSubmission?> GetApplicationAsync(string applicationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExternalApplicationStatus> SetStatusAsync(string applicationId, string statusCode, string statusText, string? detail, bool terminal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class CallbackHttp : HttpMessageHandler, IHttpClientFactory
    {
        public ExternalStatusCallback? Callback { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://portal.invalid/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Callback = await request.Content!.ReadFromJsonAsync<ExternalStatusCallback>(cancellationToken); return new(HttpStatusCode.NoContent); }
    }
}
