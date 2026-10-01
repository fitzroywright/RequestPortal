using RequestPortal.Application.Messages;
using RequestPortal.Domain.Messages;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Messages;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class PortalMessagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 23, 59, 0, TimeSpan.Zero);

    [Fact]
    public async Task SharedStaffMailbox_PreservesAuditIdentity_AndUsesNamesOnly()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        ApplicationUser representative = CreateUser(
            "Maria Johnson",
            "maria@example.org",
            isStaff: false);
        ApplicationUser staff = CreateUser(
            "David Brown",
            "david.brown@foodforthepoorja.org",
            isStaff: true);
        database.Db.Users.AddRange(representative, staff);

        Organization organization = CreateApprovedOrganization(representative.Id, "Community Group");
        database.Db.Organizations.Add(organization);
        await database.Db.SaveChangesAsync(cancellationToken);

        CapturingRealtimeNotifier realtime = new();
        PortalMessageService service = new(database.Db, realtime);

        Guid threadId = await service.SendToSharedStaffAsync(
            representative.Id,
            organization.Id,
            requestId: null,
            threadId: null,
            subject: "Question about our request",
            body: "Can FFP confirm the next step?",
            cancellationToken);

        MailboxMessageView staffInbox = Assert.Single(
            await service.GetInboxAsync(staff.Id, isStaff: true, cancellationToken: cancellationToken));

        Assert.Equal("Maria", staffInbox.SenderName);
        Assert.Equal("Representative", staffInbox.SenderKind);
        Assert.Equal("maria@example.org", staffInbox.SenderEmail);

        await service.ReplyAsync(
            staff.Id,
            senderIsStaff: true,
            threadId,
            "I will review it and update you.",
            cancellationToken);

        MessageThreadView representativeView = Assert.IsType<MessageThreadView>(
            await service.GetThreadAsync(
                representative.Id,
                isStaff: false,
                threadId,
                cancellationToken));

        MessageDetailView staffReply = Assert.Single(
            representativeView.Messages,
            item => item.SenderKind == "FFP Staff");

        Assert.Equal("David", staffReply.SenderName);
        Assert.Null(staffReply.SenderEmail);

        MailboxMessageView sharedSent = Assert.Single(
            await service.GetSentAsync(
                staff.Id,
                isStaff: true,
                cancellationToken: cancellationToken),
            item => item.ThreadId == threadId);

        Assert.Equal("David", sharedSent.SenderName);
        Assert.Null(sharedSent.SenderEmail);

        PortalMessage persistedStaffMessage = Assert.Single(
            database.Db.PortalMessages,
            item => item.SenderUserId == staff.Id);

        Assert.Equal(staff.Id, persistedStaffMessage.SenderUserId);
        Assert.Equal(PortalMessageSenderKind.FfpStaff, persistedStaffMessage.SenderKind);
    }

    [Fact]
    public async Task RepresentativeToRepresentative_UsesDisplayName_AndRequiresSameOrganization()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        ApplicationUser maria = CreateUser("Maria Johnson", "maria@example.org", false);
        ApplicationUser andre = CreateUser("Andre Williams", "andre@example.org", false);
        ApplicationUser outsider = CreateUser("Peter Brown", "peter@other.example", false);
        database.Db.Users.AddRange(maria, andre, outsider);

        Organization organization = CreateApprovedOrganization(maria.Id, "Community Group");
        OrganizationMembership andreMembership =
            organization.RequestMembership(Guid.NewGuid(), andre.Id, Now.AddMinutes(2));
        andreMembership.Approve(maria.Id, Now.AddMinutes(3));

        Organization otherOrganization = CreateApprovedOrganization(outsider.Id, "Other Group");
        database.Db.Organizations.AddRange(organization, otherOrganization);
        await database.Db.SaveChangesAsync(cancellationToken);

        PortalMessageService service = new(database.Db, new CapturingRealtimeNotifier());

        Guid threadId = await service.SendAsync(
            maria.Id,
            senderIsStaff: false,
            organization.Id,
            requestId: null,
            threadId: null,
            subject: "Meeting documents",
            body: "Andre, I uploaded the final documents.",
            [andre.Id],
            cancellationToken);

        MailboxMessageView andreInbox = Assert.Single(
            await service.GetInboxAsync(andre.Id, isStaff: false, cancellationToken: cancellationToken));

        Assert.Equal("Maria", andreInbox.SenderName);
        Assert.Null(andreInbox.SenderEmail);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SendAsync(
                maria.Id,
                senderIsStaff: false,
                organization.Id,
                requestId: null,
                threadId: null,
                subject: "Not allowed",
                body: "This must not cross organization boundaries.",
                [outsider.Id],
                cancellationToken));

        Assert.Equal(threadId, andreInbox.ThreadId);
    }

    [Fact]
    public async Task SystemMessage_AppearsForRepresentativeAndSharedStaffInbox()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        ApplicationUser representative = CreateUser("Maria Johnson", "maria@example.org", false);
        ApplicationUser staff = CreateUser("David Brown", "david.brown@foodforthepoorja.org", true);
        database.Db.Users.AddRange(representative, staff);

        Organization organization = CreateApprovedOrganization(representative.Id, "Community Group");
        database.Db.Organizations.Add(organization);
        await database.Db.SaveChangesAsync(cancellationToken);

        CapturingRealtimeNotifier realtime = new();
        PortalMessageService service = new(database.Db, realtime);

        Guid threadId = await service.SendSystemMessageAsync(
            organization.Id,
            requestId: null,
            threadId: null,
            subject: "Information required",
            body: "Please upload the signed authorization letter.",
            [representative.Id],
            cancellationToken);

        MailboxMessageView representativeInbox = Assert.Single(
            await service.GetInboxAsync(representative.Id, false, cancellationToken: cancellationToken));
        MailboxMessageView staffInbox = Assert.Single(
            await service.GetInboxAsync(staff.Id, true, cancellationToken: cancellationToken));

        Assert.Equal("FFP Request Processing", representativeInbox.SenderName);
        Assert.Equal("FFP", representativeInbox.SenderKind);
        Assert.Equal(threadId, staffInbox.ThreadId);
        Assert.Contains(realtime.Messages, item =>
            item.ThreadId == threadId &&
            item.RecipientUserIds.Contains(representative.Id) &&
            item.RecipientUserIds.Contains(staff.Id));
    }

    [Fact]
    public async Task OrganizationBroadcast_RequiresAdministratorMembership()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        ApplicationUser administrator = CreateUser("Maria Johnson", "maria@example.org", false);
        ApplicationUser member = CreateUser("Andre Williams", "andre@example.org", false);
        ApplicationUser otherMember = CreateUser("Peter Brown", "peter@example.org", false);
        database.Db.Users.AddRange(administrator, member, otherMember);

        Organization organization = CreateApprovedOrganization(administrator.Id, "Community Group");

        OrganizationMembership memberMembership =
            organization.RequestMembership(Guid.NewGuid(), member.Id, Now.AddMinutes(2));
        memberMembership.Approve(administrator.Id, Now.AddMinutes(3));

        OrganizationMembership otherMembership =
            organization.RequestMembership(Guid.NewGuid(), otherMember.Id, Now.AddMinutes(4));
        otherMembership.Approve(administrator.Id, Now.AddMinutes(5));

        database.Db.Organizations.Add(organization);
        await database.Db.SaveChangesAsync(cancellationToken);

        PortalMessageService service = new(database.Db, new CapturingRealtimeNotifier());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SendAsync(
                member.Id,
                senderIsStaff: false,
                organization.Id,
                requestId: null,
                threadId: null,
                subject: "Organization notice",
                body: "Ordinary representatives cannot broadcast.",
                recipientUserIds: Array.Empty<Guid>(),
                cancellationToken,
                audience: MessageAudience.Organization));

        Guid threadId = await service.SendAsync(
            administrator.Id,
            senderIsStaff: false,
            organization.Id,
            requestId: null,
            threadId: null,
            subject: "Organization notice",
            body: "Administrator broadcast.",
            recipientUserIds: Array.Empty<Guid>(),
            cancellationToken,
            audience: MessageAudience.Organization);

        Assert.Contains(
            await service.GetInboxAsync(member.Id, false, cancellationToken: cancellationToken),
            item => item.ThreadId == threadId);
        Assert.Contains(
            await service.GetInboxAsync(otherMember.Id, false, cancellationToken: cancellationToken),
            item => item.ThreadId == threadId);
    }

    [Fact]
    public async Task SharedStaffMailbox_SupportsAssignmentCloseAndReopen()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        ApplicationUser representative = CreateUser("Maria Johnson", "maria@example.org", false);
        ApplicationUser staff = CreateUser("David Brown", "david.brown@foodforthepoorja.org", true);
        database.Db.Users.AddRange(representative, staff);

        Organization organization = CreateApprovedOrganization(representative.Id, "Community Group");
        database.Db.Organizations.Add(organization);
        await database.Db.SaveChangesAsync(cancellationToken);

        PortalMessageService service = new(database.Db, new CapturingRealtimeNotifier());

        Guid threadId = await service.SendToSharedStaffAsync(
            representative.Id,
            organization.Id,
            requestId: null,
            threadId: null,
            subject: "Please help",
            body: "I need assistance.",
            cancellationToken);

        Assert.Contains(
            await service.GetUnassignedAsync(staff.Id, cancellationToken: cancellationToken),
            item => item.ThreadId == threadId);

        await service.AssignThreadAsync(staff.Id, threadId, cancellationToken);

        Assert.DoesNotContain(
            await service.GetUnassignedAsync(staff.Id, cancellationToken: cancellationToken),
            item => item.ThreadId == threadId);
        Assert.Contains(
            await service.GetAssignedToMeAsync(staff.Id, cancellationToken: cancellationToken),
            item => item.ThreadId == threadId);

        MessageThreadView assigned = Assert.IsType<MessageThreadView>(
            await service.GetThreadAsync(staff.Id, true, threadId, cancellationToken));
        Assert.Equal(staff.Id, assigned.AssignedToStaffUserId);
        Assert.Equal("David", assigned.AssignedToStaffName);

        await service.CloseThreadAsync(staff.Id, threadId, cancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReplyAsync(
                staff.Id,
                true,
                threadId,
                "This cannot be sent while closed.",
                cancellationToken));

        Assert.Contains(
            await service.GetArchivedAsync(staff.Id, true, cancellationToken: cancellationToken),
            item => item.ThreadId == threadId);

        await service.ReopenThreadAsync(staff.Id, threadId, cancellationToken);

        MessageThreadView reopened = Assert.IsType<MessageThreadView>(
            await service.GetThreadAsync(staff.Id, true, threadId, cancellationToken));
        Assert.Null(reopened.ClosedUtc);
        Assert.Equal(staff.Id, reopened.AssignedToStaffUserId);
    }

    [Fact]
    public async Task SystemMessage_CreatesExternalNotificationRequest()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        ApplicationUser representative = CreateUser("Maria Johnson", "maria@example.org", false);
        ApplicationUser staff = CreateUser("David Brown", "david.brown@foodforthepoorja.org", true);
        database.Db.Users.AddRange(representative, staff);

        Organization organization = CreateApprovedOrganization(representative.Id, "Community Group");
        database.Db.Organizations.Add(organization);
        await database.Db.SaveChangesAsync(cancellationToken);

        PortalMessageService service = new(database.Db, new CapturingRealtimeNotifier());

        await service.SendSystemMessageAsync(
            organization.Id,
            requestId: null,
            threadId: null,
            subject: "Information required",
            body: "Please upload the signed letter.",
            [representative.Id],
            cancellationToken);

        Assert.Contains(
            database.Db.OutboxMessages,
            message => message.MessageType.EndsWith(
                nameof(RequestPortal.Common.Messaging.PortalMessageNotificationRequestedMessage),
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task MessageAttachment_StaysWithinThreadAndOrganizationScope()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PostgreSqlTestDatabase database = await PostgreSqlTestDatabase.CreateAsync(cancellationToken);

        ApplicationUser representative = CreateUser("Maria Johnson", "maria@example.org", false);
        ApplicationUser staff = CreateUser("David Brown", "david.brown@foodforthepoorja.org", true);
        database.Db.Users.AddRange(representative, staff);

        Organization organization = CreateApprovedOrganization(representative.Id, "Community Group");
        database.Db.Organizations.Add(organization);

        var request = new RequestPortal.Domain.Requests.AssistanceRequest(
            Guid.NewGuid(),
            "RP-ATTACH-001",
            organization.Id,
            representative.Id,
            Now);
        request.SetSummary("Attachment test");
        database.Db.AssistanceRequests.Add(request);

        Guid documentId = Guid.NewGuid();
        database.Db.Documents.Add(
            new RequestPortal.Domain.Documents.StoredDocument(
                documentId,
                RequestPortal.Domain.Documents.DocumentOwnerType.AssistanceRequest,
                request.Id,
                organization.Id,
                RequestPortal.Domain.Documents.DocumentType.RequestSupport,
                "authorization.pdf",
                "application/pdf",
                1024,
                "test/authorization.pdf",
                representative.Id,
                Now));

        await database.Db.SaveChangesAsync(cancellationToken);

        PortalMessageService service = new(database.Db, new CapturingRealtimeNotifier());

        Guid threadId = await service.SendToSharedStaffAsync(
            representative.Id,
            organization.Id,
            request.Id,
            threadId: null,
            subject: "Authorization",
            body: "The authorization is attached.",
            cancellationToken: cancellationToken,
            attachmentDocumentIds: [documentId]);

        MessageThreadView thread = Assert.IsType<MessageThreadView>(
            await service.GetThreadAsync(
                staff.Id,
                isStaff: true,
                threadId,
                cancellationToken));

        MessageDetailView message = Assert.Single(thread.Messages);
        MessageAttachmentView attachment = Assert.Single(message.Attachments);
        Assert.Equal(documentId, attachment.DocumentId);
        Assert.Equal("authorization.pdf", attachment.FileName);

        Assert.True(await service.CanAccessAttachmentAsync(
            representative.Id,
            isStaff: false,
            threadId,
            documentId,
            cancellationToken));
        Assert.True(await service.CanAccessAttachmentAsync(
            staff.Id,
            isStaff: true,
            threadId,
            documentId,
            cancellationToken));
    }

    private static ApplicationUser CreateUser(
        string displayName,
        string email,
        bool isStaff)
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
            DisplayName = displayName,
            IsInternalStaff = isStaff,
            CreatedUtc = Now
        };
    }

    private static Organization CreateApprovedOrganization(
        Guid ownerUserId,
        string name)
    {
        Organization organization = new(
            Guid.NewGuid(),
            name,
            "JM",
            ownerUserId,
            Now);

        organization.RequestMembership(
            Guid.NewGuid(),
            ownerUserId,
            Now);

        organization.Approve(
            Guid.NewGuid(),
            Now.AddMinutes(1));

        return organization;
    }

    private sealed class CapturingRealtimeNotifier : IPortalMessageRealtimeNotifier
    {
        public List<PortalMessageCreated> Messages { get; } = [];

        public Task NotifyAsync(
            PortalMessageCreated message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
