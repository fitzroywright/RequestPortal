using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Portal;
using RequestPortal.Application.Requests;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Auditing;
using RequestPortal.Domain.Messages;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.ReferenceData;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Messages;
using RequestPortal.Infrastructure.Messaging;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Requests;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class NotificationRoutingTests
{
    static readonly QuickIntakeSubmission Submission = new("Routing", "Fixture", new DateOnly(1980, 1, 1), "Male", "Single",
        "8765550199", null, "Fixture address", "Fixture community", "Kingston", "Unemployed", 0, false, false, ["None"], "Food");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntakeRoutesToDesignatedStaffAndApplicantWithoutDuplicatesOrBroadcast(bool signedIn)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.Create(ct);
        Guid? applicant = signedIn ? f.Applicant.Id : null;
        var quick = new QuickIntakeService(f.Db);
        Guid id = Guid.NewGuid();
        var result = await quick.CreateAsync(Submission, ct, id, applicant);
        Assert.Equal(result, await quick.CreateAsync(Submission, ct, id, applicant));
        await f.Dispatcher.DispatchBatchAsync(cancellationToken: ct);
        var request = await f.Db.AssistanceRequests.SingleAsync(r => r.Id == id, ct);
        Guid owner = request.SubmittedByUserId ?? request.CreatedByUserId;
        var message = Assert.Single(await f.Db.PortalMessages.ToArrayAsync(ct));
        Assert.Equal(new[] { owner, f.Intake.Id }.Order(), (await f.Db.MessageRecipients.Select(r => r.UserId).ToArrayAsync(ct)).Order());
        Assert.Equal(new[] { owner, f.Intake.Id }.Order(), Assert.Single(f.Realtime.Messages).RecipientUserIds.Order());
        Assert.Empty(await f.Service.GetInboxAsync(f.Unrelated.Id, true, cancellationToken: ct));
        Assert.Equal(0, await f.Service.GetUnreadCountAsync(f.Unrelated.Id, true, ct));
        Assert.Null(await f.Service.GetThreadAsync(f.Unrelated.Id, true, message.ThreadId, ct));
        Assert.Single(await f.Service.GetInboxAsync(owner, false, cancellationToken: ct));
        Assert.Single(await f.Service.GetInboxAsync(f.Intake.Id, true, cancellationToken: ct));
        Assert.Single(await f.Db.OutboxMessages.Where(o => o.MessageType == QuickIntakeClientRelationsEmailRequestedMessage.AwaitingReferenceMessageType).ToArrayAsync(ct));
        await f.Dispatcher.DispatchBatchAsync(cancellationToken: ct);
        Assert.Single(await f.Db.PortalMessages.ToArrayAsync(ct));
        Assert.Single(f.Realtime.Messages);
        Assert.NotEmpty(request.PublicReference);
    }

    [Fact]
    public async Task AssignmentStatusAndRepliesUseAssignedReviewerAndCurrentSubscriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.Create(ct);
        var org = new Organization(Guid.NewGuid(), "Fixture organization", "JM", f.Applicant.Id, DateTimeOffset.UtcNow);
        org.RequestMembership(Guid.NewGuid(), f.Applicant.Id, DateTimeOffset.UtcNow);
        org.Approve(f.Intake.Id, DateTimeOffset.UtcNow);
        var member = org.RequestMembership(Guid.NewGuid(), f.UnrelatedPublic.Id, DateTimeOffset.UtcNow);
        member.Approve(f.Intake.Id, DateTimeOffset.UtcNow); member.PromoteToAdministrator();
        member.SetNotificationSubscriptions(false, true, false);
        f.Db.Organizations.Add(org);
        var request = new RequestPortal.Domain.Requests.AssistanceRequest(Guid.NewGuid(), "RP-26-90001", org.Id, f.Applicant.Id, DateTimeOffset.UtcNow);
        f.Db.AssistanceRequests.Add(request); await f.Db.SaveChangesAsync(ct);
        Guid thread = await f.Service.SendSystemMessageAsync(org.Id, request.Id, null, "Fixture", "Fixture confirmation", [f.Applicant.Id, f.Intake.Id], ct);
        await f.Service.AssignThreadAsync(f.Intake.Id, thread, ct);
        int count = f.Db.PortalMessages.Count();
        await f.Service.AssignThreadAsync(f.Intake.Id, thread, ct);
        Assert.Equal(count, f.Db.PortalMessages.Count());
        Assert.Equal(new[] { f.Intake.Id }, f.Realtime.Messages.Last().RecipientUserIds);
        Assert.Null(await f.Service.GetThreadAsync(f.Applicant.Id, false, f.Realtime.Messages.Last().ThreadId, ct));
        Assert.DoesNotContain((await f.Service.GetThreadAsync(f.Applicant.Id, false, thread, ct))!.Messages,
            m => m.Body.Contains("assigned to you"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.AssignThreadAsync(f.Unrelated.Id, thread, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReplyAsync(f.Unrelated.Id, true, thread, "Not allowed", ct));
        var status = new RequestStatusChangedMessage(Guid.NewGuid(), DateTimeOffset.UtcNow, request.Id, (int)request.Status);
        f.Db.OutboxMessages.Add(new OutboxMessage { Id = status.MessageId, MessageType = typeof(RequestStatusChangedMessage).FullName!, Payload = JsonSerializer.Serialize(status), OccurredUtc = status.OccurredUtc });
        await f.Db.SaveChangesAsync(ct); await f.Dispatcher.DispatchBatchAsync(cancellationToken: ct);
        Assert.Equal(new[] { f.Applicant.Id, f.Intake.Id, f.UnrelatedPublic.Id }.Order(), f.Realtime.Messages.Last().RecipientUserIds.Order());
        await f.Service.ReplyAsync(f.Applicant.Id, false, thread, "Fixture reply", ct);
        Assert.DoesNotContain(f.Unrelated.Id, f.Realtime.Messages.Last().RecipientUserIds);
        Assert.Contains(f.Intake.Id, f.Realtime.Messages.Last().RecipientUserIds);
        org.RevokeMembership(member.Id, f.Intake.Id, DateTimeOffset.UtcNow, "Fixture removal");
        await f.Db.SaveChangesAsync(ct);
        Assert.Null(await f.Service.GetThreadAsync(f.UnrelatedPublic.Id, false, thread, ct));
    }

    [Fact]
    public async Task AssignedReviewerReplacesIntakeForSubsequentStatusAndApplicantReplies()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.Create(ct);
        var created = await new QuickIntakeService(f.Db).CreateAsync(Submission, ct);
        await f.Dispatcher.DispatchBatchAsync(cancellationToken: ct);
        var thread = await f.Db.MessageThreads.SingleAsync(t => t.Id == created.RequestId, ct);
        thread.Assign(f.Unrelated.Id, DateTimeOffset.UtcNow);
        await f.Db.SaveChangesAsync(ct);
        var request = await f.Db.AssistanceRequests.SingleAsync(r => r.Id == created.RequestId, ct);
        var status = new RequestStatusChangedMessage(Guid.NewGuid(), DateTimeOffset.UtcNow, request.Id, (int)request.Status);
        f.Db.OutboxMessages.Add(new OutboxMessage { Id = status.MessageId, MessageType = typeof(RequestStatusChangedMessage).FullName!, Payload = JsonSerializer.Serialize(status), OccurredUtc = status.OccurredUtc });
        await f.Db.SaveChangesAsync(ct); await f.Dispatcher.DispatchBatchAsync(cancellationToken: ct);
        Assert.Contains(f.Unrelated.Id, f.Realtime.Messages.Last().RecipientUserIds);
        Assert.DoesNotContain(f.Intake.Id, f.Realtime.Messages.Last().RecipientUserIds);
        await f.Service.ReplyAsync(request.CreatedByUserId, false, thread.Id, "Fixture reply", ct);
        Assert.Equal(new[] { f.Unrelated.Id }, f.Realtime.Messages.Last().RecipientUserIds);
        Assert.Null(await f.Service.GetThreadAsync(f.Intake.Id, true, thread.Id, ct));
    }

    [Fact]
    public async Task ConcurrentAssignmentCreatesOnePrivateNotification()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.Create(ct);
        var created = await new QuickIntakeService(f.Db).CreateAsync(Submission, ct);
        await f.Dispatcher.DispatchBatchAsync(cancellationToken: ct);
        await using var first = await Sibling(f, ct);
        await using var second = await Sibling(f, ct);
        var one = new PortalMessageService(first, f.Realtime, null!, Options.Create(new FfpManagerBridgeOptions()));
        var two = new PortalMessageService(second, f.Realtime, null!, Options.Create(new FfpManagerBridgeOptions()));
        await Task.WhenAll(one.AssignThreadAsync(f.Intake.Id, created.RequestId, ct), two.AssignThreadAsync(f.Intake.Id, created.RequestId, ct));
        Assert.Equal(2, await f.Db.PortalMessages.CountAsync(ct));
        Assert.Equal(2, f.Realtime.Messages.Count);
    }

    static async Task<RequestPortalDbContext> Sibling(Fixture f, CancellationToken ct)
    {
        var connection = f.Db.Database.GetConnectionString()!;
        var db = new RequestPortalDbContext(new DbContextOptionsBuilder<RequestPortalDbContext>().UseNpgsql(connection).Options);
        await db.Database.OpenConnectionAsync(ct);
        return db;
    }

    [Fact]
    public async Task HistoricalBroadcastRowsArePreservedButCannotGrantAccessOrUnreadCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.Create(ct);
        var created = await new QuickIntakeService(f.Db).CreateAsync(Submission, ct);
        await f.Dispatcher.DispatchBatchAsync(cancellationToken: ct);
        var message = await f.Db.PortalMessages.SingleAsync(ct);
        // Reproduce the old all-staff expansion without deleting or rewriting history.
        f.Db.MessageRecipients.Add(new MessageRecipient(message.Id, f.Unrelated.Id));
        f.Db.AuditEntries.Add(new AuditEntry(Guid.NewGuid(), f.Intake.Id, "HistoricalFixture", "PortalMessage", message.Id, null, DateTimeOffset.UtcNow));
        await f.Db.SaveChangesAsync(ct);
        int recipients = await f.Db.MessageRecipients.CountAsync(ct), audits = await f.Db.AuditEntries.CountAsync(ct);
        Assert.Empty(await f.Service.GetInboxAsync(f.Unrelated.Id, true, cancellationToken: ct));
        Assert.Equal(0, await f.Service.GetUnreadCountAsync(f.Unrelated.Id, true, ct));
        Assert.Null(await f.Service.GetThreadAsync(f.Unrelated.Id, true, message.ThreadId, ct));
        Assert.False(await f.Service.CanAccessAttachmentAsync(f.Unrelated.Id, true, message.ThreadId, Guid.NewGuid(), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.ReopenThreadAsync(f.Unrelated.Id, message.ThreadId, ct));
        await f.Service.CloseThreadAsync(f.Intake.Id, message.ThreadId, ct);
        Assert.Empty(await f.Service.GetArchivedAsync(f.Unrelated.Id, true, cancellationToken: ct));
        Assert.Equal(recipients, await f.Db.MessageRecipients.CountAsync(ct));
        Assert.Equal(audits, await f.Db.AuditEntries.CountAsync(ct));
        Assert.Single(await f.Db.PortalMessages.ToArrayAsync(ct));
        // An administrator can review a request explicitly, without a mailbox subscription.
        var adminService = new PortalMessageService(f.Db, f.Realtime, null!, Options.Create(new FfpManagerBridgeOptions()),
            routing: new StaffNotificationRouting(f.Db, new AdminAccess(f.Unrelated.Id)));
        Assert.NotNull(await adminService.GetThreadAsync(f.Unrelated.Id, true, message.ThreadId, ct));
        Assert.Empty(await adminService.GetInboxAsync(f.Unrelated.Id, true, cancellationToken: ct));
    }

    [Fact]
    public async Task MissingConfigurationRetainsOperationalAlertsAndRecordsActionableWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.Create(ct);
        f.Db.UserClaims.RemoveRange(f.Db.UserClaims); await f.Db.SaveChangesAsync(ct);
        await new QuickIntakeService(f.Db).CreateAsync(Submission, ct);
        await f.Dispatcher.DispatchBatchAsync(cancellationToken: ct);
        Assert.DoesNotContain(f.Realtime.Messages.SelectMany(m => m.RecipientUserIds), id => id == f.Intake.Id || id == f.Unrelated.Id);
        Guid alert = await f.Service.SendStaffAlertAsync(null, null, "Operational fixture", "Sanitized diagnostic", ct);
        Assert.Empty(f.Realtime.Messages.Last().RecipientUserIds);
        Assert.Contains(await f.Db.AuditEntries.ToArrayAsync(ct), a => a.Action == "NotificationRoutingMissing" && a.Outcome == "Warning" && a.Detail!.Contains("Admin > Notification Routing"));
        Assert.True(await f.Db.PortalMessages.AnyAsync(m => m.ThreadId == alert, ct));
        f.Db.UserClaims.Add(new IdentityUserClaim<Guid> { UserId = f.Intake.Id, ClaimType = StaffNotificationRouting.ClaimType, ClaimValue = StaffNotificationRouting.Operations });
        await f.Db.SaveChangesAsync(ct);
        await f.Service.SendStaffAlertAsync(null, null, "Operational fixture", "Sanitized diagnostic", ct);
        Assert.Equal(new[] { f.Intake.Id }, f.Realtime.Messages.Last().RecipientUserIds);
    }

    sealed class AdminAccess(Guid admin) : IStaffNotificationAccess
    {
        public Task<bool> IsEligibleAsync(Guid id, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> IsAdministratorAsync(Guid id, CancellationToken ct) => Task.FromResult(id == admin);
    }
    sealed class Fixture(PostgreSqlTestDatabase database) : IAsyncDisposable
    {
        public RequestPortalDbContext Db => database.Db;
        public ApplicationUser Applicant = User(false), UnrelatedPublic = User(false), Intake = User(true), Unrelated = User(true);
        public Realtime Realtime = new();
        public PortalMessageService Service = null!;
        public OutboxDispatcher Dispatcher = null!;
        public static async Task<Fixture> Create(CancellationToken ct)
        {
            var f = new Fixture(await PostgreSqlTestDatabase.CreateAsync(ct));
            f.Db.Users.AddRange(f.Applicant, f.UnrelatedPublic, f.Intake, f.Unrelated);
            f.Db.UnitsOfMeasure.Add(new UnitOfMeasure(Guid.NewGuid(), "EA", "Each"));
            f.Db.UserClaims.Add(new IdentityUserClaim<Guid> { UserId = f.Intake.Id, ClaimType = StaffNotificationRouting.ClaimType, ClaimValue = StaffNotificationRouting.Intake });
            await f.Db.SaveChangesAsync(ct);
            f.Service = new(f.Db, f.Realtime, null!, Options.Create(new FfpManagerBridgeOptions()));
            f.Dispatcher = new(f.Db, new Sender(), null!, Options.Create(new FfpManagerBridgeOptions()), Options.Create(new NotificationOptions()), NullLogger<OutboxDispatcher>.Instance, new Flow(), f.Service);
            return f;
        }
        static ApplicationUser User(bool staff) => new() { Id = Guid.NewGuid(), UserName = Guid.NewGuid().ToString(), DisplayName = "Routing fixture", IsInternalStaff = staff, CreatedUtc = DateTimeOffset.UtcNow };
        public ValueTask DisposeAsync() => database.DisposeAsync();
    }
    sealed class Realtime : IPortalMessageRealtimeNotifier
    {
        public List<PortalMessageCreated> Messages = [];
        public Task NotifyAsync(PortalMessageCreated message, CancellationToken ct = default) { Messages.Add(message); return Task.CompletedTask; }
    }
    sealed class Sender : INotificationSender
    { public Task SendAsync(NotificationEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask; }
    sealed class Flow : IRequestPortalOperationsFlowPublisher
    { public bool TryEnqueue(Guid id, string stage, DateTimeOffset when, string? detail = null, bool failed = false) => true; }
}
