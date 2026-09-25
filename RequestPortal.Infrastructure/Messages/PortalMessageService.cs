using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Messages;
using RequestPortal.Domain.Messages;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Messages;

public sealed class PortalMessageService : IPortalMessageService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IPortalMessageRealtimeNotifier realtimeNotifier;

    public PortalMessageService(
        RequestPortalDbContext dbContext,
        IPortalMessageRealtimeNotifier realtimeNotifier)
    {
        this.dbContext = dbContext;
        this.realtimeNotifier = realtimeNotifier;
    }

    public async Task<int> GetUnreadCountAsync(
        Guid userId,
        bool isStaff,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.MessageRecipients
            .AsNoTracking()
            .CountAsync(
                recipient =>
                    recipient.UserId == userId &&
                    recipient.ReadUtc == null &&
                    recipient.ArchivedUtc == null,
                cancellationToken);
    }

    public async Task<IReadOnlyList<MailboxMessageView>> GetInboxAsync(
        Guid userId,
        bool isStaff,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        int boundedTake = Math.Clamp(take, 1, 250);

        var rows = await (
            from recipient in dbContext.MessageRecipients.AsNoTracking()
            join message in dbContext.PortalMessages.AsNoTracking()
                on recipient.MessageId equals message.Id
            join thread in dbContext.MessageThreads.AsNoTracking()
                on message.ThreadId equals thread.Id
            join sender in dbContext.Users.AsNoTracking()
                on message.SenderUserId equals (Guid?)sender.Id into senders
            from sender in senders.DefaultIfEmpty()
            join organization in dbContext.Organizations.AsNoTracking()
                on thread.OrganizationId equals organization.Id into organizations
            from organization in organizations.DefaultIfEmpty()
            join request in dbContext.AssistanceRequests.AsNoTracking()
                on thread.RequestId equals request.Id into requests
            from request in requests.DefaultIfEmpty()
            where recipient.UserId == userId && recipient.ArchivedUtc == null
            orderby message.SentUtc descending
            select new
            {
                Message = message,
                Thread = thread,
                Recipient = recipient,
                Sender = sender,
                OrganizationName = organization == null ? null : organization.Name,
                RequestReference = request == null ? null : request.PublicReference
            })
            .Take(boundedTake)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => ToMailboxView(
                row.Message,
                row.Thread,
                row.Recipient.ReadUtc,
                row.Sender,
                row.OrganizationName,
                row.RequestReference,
                isStaff))
            .ToArray();
    }

    public async Task<IReadOnlyList<MailboxMessageView>> GetSentAsync(
        Guid userId,
        bool isStaff,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        int boundedTake = Math.Clamp(take, 1, 250);

        var rows = await (
            from message in dbContext.PortalMessages.AsNoTracking()
            join thread in dbContext.MessageThreads.AsNoTracking()
                on message.ThreadId equals thread.Id
            join sender in dbContext.Users.AsNoTracking()
                on message.SenderUserId equals (Guid?)sender.Id into senders
            from sender in senders.DefaultIfEmpty()
            join organization in dbContext.Organizations.AsNoTracking()
                on thread.OrganizationId equals organization.Id into organizations
            from organization in organizations.DefaultIfEmpty()
            join request in dbContext.AssistanceRequests.AsNoTracking()
                on thread.RequestId equals request.Id into requests
            from request in requests.DefaultIfEmpty()
            where isStaff
                ? message.SenderKind == PortalMessageSenderKind.FfpStaff
                : message.SenderUserId == userId
            orderby message.SentUtc descending
            select new
            {
                Message = message,
                Thread = thread,
                Sender = sender,
                OrganizationName = organization == null ? null : organization.Name,
                RequestReference = request == null ? null : request.PublicReference
            })
            .Take(boundedTake)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => ToMailboxView(
                row.Message,
                row.Thread,
                null,
                row.Sender,
                row.OrganizationName,
                row.RequestReference,
                isStaff))
            .ToArray();
    }

    public async Task<MessageThreadView?> GetThreadAsync(
        Guid userId,
        bool isStaff,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        MessageThread? thread = await dbContext.MessageThreads
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);

        if (thread is null || !await CanViewThreadAsync(userId, isStaff, thread, cancellationToken))
        {
            return null;
        }

        await MarkThreadReadAsync(userId, threadId, cancellationToken);

        string? organizationName = thread.OrganizationId.HasValue
            ? await dbContext.Organizations.AsNoTracking()
                .Where(item => item.Id == thread.OrganizationId.Value)
                .Select(item => item.Name)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        string? requestReference = thread.RequestId.HasValue
            ? await dbContext.AssistanceRequests.AsNoTracking()
                .Where(item => item.Id == thread.RequestId.Value)
                .Select(item => item.PublicReference)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var messages = await (
            from message in dbContext.PortalMessages.AsNoTracking()
            join sender in dbContext.Users.AsNoTracking()
                on message.SenderUserId equals (Guid?)sender.Id into senders
            from sender in senders.DefaultIfEmpty()
            where message.ThreadId == threadId
            orderby message.SentUtc
            select new
            {
                Message = message,
                Sender = sender
            })
            .ToListAsync(cancellationToken);

        MessageDetailView[] projected = messages
            .Select(row =>
            {
                (string senderName, string? senderEmail, string senderKind) =
                    ProjectSender(row.Message, row.Sender, isStaff);
                return new MessageDetailView(
                    row.Message.Id,
                    senderName,
                    senderEmail,
                    senderKind,
                    row.Message.Body,
                    row.Message.SentUtc,
                    row.Message.SenderUserId == userId);
            })
            .ToArray();

        return new MessageThreadView(
            thread.Id,
            thread.Subject,
            thread.OrganizationId,
            organizationName,
            thread.RequestId,
            requestReference,
            projected);
    }

    public async Task<IReadOnlyList<MessageRecipientOption>> GetOrganizationRecipientsAsync(
        Guid userId,
        bool isStaff,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        if (!isStaff)
        {
            await EnsureApprovedMembershipAsync(userId, organizationId, cancellationToken);
        }

        var rows = await (
            from membership in dbContext.OrganizationMemberships.AsNoTracking()
            join user in dbContext.Users.AsNoTracking()
                on membership.UserId equals user.Id
            where membership.OrganizationId == organizationId &&
                  membership.Status == MembershipStatus.Approved &&
                  user.Id != userId
            orderby user.DisplayName
            select new
            {
                user.Id,
                user.DisplayName,
                user.Email,
                user.IsInternalStaff
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new MessageRecipientOption(
                row.Id,
                isStaff ? row.DisplayName : FirstName(row.DisplayName, row.Email),
                isStaff ? row.Email : null,
                row.IsInternalStaff))
            .ToArray();
    }

    public async Task<Guid> SendToSharedStaffAsync(
        Guid senderUserId,
        Guid organizationId,
        Guid? requestId,
        Guid? threadId,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        await EnsureApprovedMembershipAsync(senderUserId, organizationId, cancellationToken);
        await ValidateRequestScopeAsync(organizationId, requestId, cancellationToken);

        Guid[] staffUserIds = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.IsInternalStaff)
            .Select(user => user.Id)
            .ToArrayAsync(cancellationToken);

        if (staffUserIds.Length == 0)
        {
            throw new InvalidOperationException("No FFP staff mailbox identities are available.");
        }

        return await SendInternalAsync(
            senderUserId,
            PortalMessageSenderKind.Representative,
            organizationId,
            requestId,
            threadId,
            subject,
            body,
            staffUserIds,
            staffVisible: true,
            cancellationToken);
    }

    public async Task<Guid> SendAsync(
        Guid senderUserId,
        bool senderIsStaff,
        Guid? organizationId,
        Guid? requestId,
        Guid? threadId,
        string subject,
        string body,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        if (recipientUserIds.Count == 0)
        {
            throw new ArgumentException("At least one message recipient is required.", nameof(recipientUserIds));
        }

        if (!senderIsStaff)
        {
            if (!organizationId.HasValue)
            {
                throw new InvalidOperationException("Representative messages must belong to an organization.");
            }

            await EnsureApprovedMembershipAsync(senderUserId, organizationId.Value, cancellationToken);
            await EnsureRepresentativeRecipientsAsync(
                senderUserId,
                organizationId.Value,
                recipientUserIds,
                cancellationToken);
        }

        await ValidateRequestScopeAsync(organizationId, requestId, cancellationToken);

        bool staffVisible = senderIsStaff ||
            await dbContext.Users.AsNoTracking()
                .AnyAsync(
                    user => recipientUserIds.Contains(user.Id) && user.IsInternalStaff,
                    cancellationToken);

        return await SendInternalAsync(
            senderUserId,
            senderIsStaff ? PortalMessageSenderKind.FfpStaff : PortalMessageSenderKind.Representative,
            organizationId,
            requestId,
            threadId,
            subject,
            body,
            recipientUserIds,
            staffVisible,
            cancellationToken);
    }

    public async Task<Guid> ReplyAsync(
        Guid senderUserId,
        bool senderIsStaff,
        Guid threadId,
        string body,
        CancellationToken cancellationToken = default)
    {
        MessageThread thread = await dbContext.MessageThreads
            .SingleAsync(item => item.Id == threadId, cancellationToken);

        if (!await CanViewThreadAsync(senderUserId, senderIsStaff, thread, cancellationToken))
        {
            throw new UnauthorizedAccessException("The current user cannot reply to this conversation.");
        }

        Guid[] participantUserIds = await (
            from message in dbContext.PortalMessages.AsNoTracking()
            where message.ThreadId == threadId && message.SenderUserId != null
            select message.SenderUserId!.Value)
            .Union(
                from recipient in dbContext.MessageRecipients.AsNoTracking()
                join message in dbContext.PortalMessages.AsNoTracking()
                    on recipient.MessageId equals message.Id
                where message.ThreadId == threadId
                select recipient.UserId)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        Guid[] recipients;
        if (senderIsStaff)
        {
            recipients = await dbContext.Users
                .AsNoTracking()
                .Where(user =>
                    participantUserIds.Contains(user.Id) &&
                    !user.IsInternalStaff &&
                    user.Id != senderUserId)
                .Select(user => user.Id)
                .ToArrayAsync(cancellationToken);
        }
        else if (thread.StaffVisible)
        {
            recipients = await dbContext.Users
                .AsNoTracking()
                .Where(user => user.IsInternalStaff)
                .Select(user => user.Id)
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            recipients = participantUserIds
                .Where(id => id != senderUserId)
                .Distinct()
                .ToArray();
        }

        if (recipients.Length == 0)
        {
            throw new InvalidOperationException("No eligible recipients are available for this conversation.");
        }

        return await SendInternalAsync(
            senderUserId,
            senderIsStaff ? PortalMessageSenderKind.FfpStaff : PortalMessageSenderKind.Representative,
            thread.OrganizationId,
            thread.RequestId,
            thread.Id,
            thread.Subject,
            body,
            recipients,
            thread.StaffVisible,
            cancellationToken);
    }

    public async Task<Guid> SendSystemMessageAsync(
        Guid? organizationId,
        Guid? requestId,
        Guid? threadId,
        string subject,
        string body,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken = default)
    {
        if (recipientUserIds.Count == 0)
        {
            throw new ArgumentException("At least one message recipient is required.", nameof(recipientUserIds));
        }

        await ValidateRequestScopeAsync(organizationId, requestId, cancellationToken);

        MessageThread thread = await ResolveThreadAsync(
            threadId,
            organizationId,
            requestId,
            subject,
            staffVisible: true,
            cancellationToken);

        PortalMessage message = new(
            Guid.NewGuid(),
            thread.Id,
            null,
            PortalMessageSenderKind.System,
            "FFP Request Processing",
            body,
            DateTimeOffset.UtcNow);

        Guid[] staffUserIds = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.IsInternalStaff)
            .Select(user => user.Id)
            .ToArrayAsync(cancellationToken);
        Guid[] allRecipients = recipientUserIds
            .Concat(staffUserIds)
            .Distinct()
            .ToArray();

        dbContext.PortalMessages.Add(message);
        AddRecipients(message.Id, allRecipients, senderUserId: null);
        await dbContext.SaveChangesAsync(cancellationToken);

        await NotifyAsync(message, thread, allRecipients, cancellationToken);
        return thread.Id;
    }

    public async Task MarkThreadReadAsync(
        Guid userId,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        Guid[] messageIds = await dbContext.PortalMessages
            .AsNoTracking()
            .Where(message => message.ThreadId == threadId)
            .Select(message => message.Id)
            .ToArrayAsync(cancellationToken);

        if (messageIds.Length == 0)
        {
            return;
        }

        List<MessageRecipient> recipients = await dbContext.MessageRecipients
            .Where(recipient =>
                recipient.UserId == userId &&
                messageIds.Contains(recipient.MessageId) &&
                recipient.ReadUtc == null)
            .ToListAsync(cancellationToken);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (MessageRecipient recipient in recipients)
        {
            recipient.MarkRead(now);
        }

        if (recipients.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<Guid> SendInternalAsync(
        Guid senderUserId,
        PortalMessageSenderKind senderKind,
        Guid? organizationId,
        Guid? requestId,
        Guid? threadId,
        string subject,
        string body,
        IReadOnlyCollection<Guid> recipientUserIds,
        bool staffVisible,
        CancellationToken cancellationToken)
    {
        ApplicationUserProjection sender = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.Id == senderUserId)
            .Select(user => new ApplicationUserProjection(
                user.Id,
                user.DisplayName,
                user.Email,
                user.IsInternalStaff))
            .SingleAsync(cancellationToken);

        MessageThread thread = await ResolveThreadAsync(
            threadId,
            organizationId,
            requestId,
            subject,
            staffVisible,
            cancellationToken);

        PortalMessage message = new(
            Guid.NewGuid(),
            thread.Id,
            senderUserId,
            senderKind,
            FirstName(sender.DisplayName, sender.Email),
            body,
            DateTimeOffset.UtcNow);

        dbContext.PortalMessages.Add(message);
        Guid[] recipients = recipientUserIds
            .Where(id => id != senderUserId)
            .Distinct()
            .ToArray();
        AddRecipients(message.Id, recipients, senderUserId);
        AddExternalNotificationOutbox(message, thread, recipients);
        await dbContext.SaveChangesAsync(cancellationToken);

        await NotifyAsync(message, thread, recipients, cancellationToken);
        return thread.Id;
    }

    private async Task<MessageThread> ResolveThreadAsync(
        Guid? threadId,
        Guid? organizationId,
        Guid? requestId,
        string subject,
        bool staffVisible,
        CancellationToken cancellationToken)
    {
        if (threadId.HasValue)
        {
            MessageThread existing = await dbContext.MessageThreads
                .SingleAsync(item => item.Id == threadId.Value, cancellationToken);

            if (organizationId.HasValue && existing.OrganizationId != organizationId)
            {
                throw new InvalidOperationException("The selected conversation belongs to a different organization.");
            }
            if (requestId.HasValue && existing.RequestId != requestId)
            {
                throw new InvalidOperationException("The selected conversation belongs to a different request.");
            }
            return existing;
        }

        MessageThread created = new(
            Guid.NewGuid(),
            organizationId,
            requestId,
            subject,
            staffVisible,
            DateTimeOffset.UtcNow);
        dbContext.MessageThreads.Add(created);
        return created;
    }

    private void AddRecipients(
        Guid messageId,
        IEnumerable<Guid> recipientUserIds,
        Guid? senderUserId)
    {
        foreach (Guid userId in recipientUserIds
            .Where(id => !senderUserId.HasValue || id != senderUserId.Value)
            .Distinct())
        {
            dbContext.MessageRecipients.Add(new MessageRecipient(messageId, userId));
        }
    }

    private void AddExternalNotificationOutbox(
        PortalMessage message,
        MessageThread thread,
        IReadOnlyCollection<Guid> recipientUserIds)
    {
        PortalMessageNotificationRequestedMessage notification = new(
            Guid.NewGuid(),
            message.SentUtc,
            message.Id,
            thread.Id,
            thread.OrganizationId,
            thread.RequestId,
            message.SenderDisplayName,
            thread.Subject,
            recipientUserIds);

        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = notification.MessageId,
            MessageType = typeof(PortalMessageNotificationRequestedMessage).FullName ?? nameof(PortalMessageNotificationRequestedMessage),
            Payload = System.Text.Json.JsonSerializer.Serialize(notification),
            OccurredUtc = notification.OccurredUtc,
            AttemptCount = 0
        });
    }

    private async Task NotifyAsync(
        PortalMessage message,
        MessageThread thread,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken)
    {
        string preview = message.Body.Length <= 140
            ? message.Body
            : message.Body[..137] + "...";

        await realtimeNotifier.NotifyAsync(
            new PortalMessageCreated(
                message.Id,
                thread.Id,
                recipientUserIds,
                thread.Subject,
                message.SenderDisplayName,
                preview),
            cancellationToken);
    }

    private async Task<bool> CanViewThreadAsync(
        Guid userId,
        bool isStaff,
        MessageThread thread,
        CancellationToken cancellationToken)
    {
        if (isStaff && thread.StaffVisible)
        {
            return true;
        }

        bool sent = await dbContext.PortalMessages
            .AsNoTracking()
            .AnyAsync(
                message => message.ThreadId == thread.Id && message.SenderUserId == userId,
                cancellationToken);
        if (sent)
        {
            return true;
        }

        return await (
            from recipient in dbContext.MessageRecipients.AsNoTracking()
            join message in dbContext.PortalMessages.AsNoTracking()
                on recipient.MessageId equals message.Id
            where message.ThreadId == thread.Id && recipient.UserId == userId
            select recipient)
            .AnyAsync(cancellationToken);
    }

    private async Task EnsureApprovedMembershipAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        bool approved = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(
                membership =>
                    membership.UserId == userId &&
                    membership.OrganizationId == organizationId &&
                    membership.Status == MembershipStatus.Approved,
                cancellationToken);

        if (!approved)
        {
            throw new UnauthorizedAccessException("Approved organization membership is required for this conversation.");
        }
    }

    private async Task EnsureRepresentativeRecipientsAsync(
        Guid senderUserId,
        Guid organizationId,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken)
    {
        Guid[] distinctRecipients = recipientUserIds
            .Where(id => id != senderUserId)
            .Distinct()
            .ToArray();

        int approvedRecipients = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .CountAsync(
                membership =>
                    distinctRecipients.Contains(membership.UserId) &&
                    membership.OrganizationId == organizationId &&
                    membership.Status == MembershipStatus.Approved,
                cancellationToken);

        if (approvedRecipients != distinctRecipients.Length)
        {
            throw new UnauthorizedAccessException("Representatives may only message approved members of the same organization.");
        }
    }

    private async Task ValidateRequestScopeAsync(
        Guid? organizationId,
        Guid? requestId,
        CancellationToken cancellationToken)
    {
        if (!requestId.HasValue)
        {
            return;
        }

        Guid requestOrganizationId = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Where(request => request.Id == requestId.Value)
            .Select(request => request.OrganizationId)
            .SingleAsync(cancellationToken);

        if (organizationId.HasValue && organizationId.Value != requestOrganizationId)
        {
            throw new InvalidOperationException("The selected request belongs to a different organization.");
        }
    }

    private static MailboxMessageView ToMailboxView(
        PortalMessage message,
        MessageThread thread,
        DateTimeOffset? readUtc,
        ApplicationUser? sender,
        string? organizationName,
        string? requestReference,
        bool viewerIsStaff)
    {
        (string senderName, string? senderEmail, string senderKind) =
            ProjectSender(message, sender, viewerIsStaff);
        string preview = message.Body.Length <= 140
            ? message.Body
            : message.Body[..137] + "...";

        return new MailboxMessageView(
            message.Id,
            thread.Id,
            thread.Subject,
            senderName,
            senderEmail,
            senderKind,
            preview,
            message.SentUtc,
            readUtc,
            thread.OrganizationId,
            organizationName,
            thread.RequestId,
            requestReference);
    }

    private static (string Name, string? Email, string Kind) ProjectSender(
        PortalMessage message,
        ApplicationUser? sender,
        bool viewerIsStaff)
    {
        if (message.SenderKind == PortalMessageSenderKind.System)
        {
            return ("FFP Request Processing", null, "FFP");
        }

        string displayName = sender?.DisplayName ?? message.SenderDisplayName;
        string? email = sender?.Email;

        if (viewerIsStaff)
        {
            return (
                string.IsNullOrWhiteSpace(displayName) ? message.SenderDisplayName : displayName,
                message.SenderKind == PortalMessageSenderKind.Representative ? email : null,
                message.SenderKind == PortalMessageSenderKind.FfpStaff ? "FFP Staff" : "Representative");
        }

        return (
            FirstName(displayName, email),
            null,
            message.SenderKind == PortalMessageSenderKind.FfpStaff ? "FFP Staff" : "Representative");
    }

    private static string FirstName(string? displayName, string? email)
    {
        string candidate = string.IsNullOrWhiteSpace(displayName)
            ? email?.Split('@')[0] ?? "User"
            : displayName.Trim();

        if (candidate.Contains('@', StringComparison.Ordinal))
        {
            candidate = candidate.Split('@')[0];
        }

        int separator = candidate.IndexOfAny([' ', '\t', ',']);
        return separator > 0 ? candidate[..separator] : candidate;
    }

    private sealed record ApplicationUserProjection(
        Guid Id,
        string DisplayName,
        string? Email,
        bool IsInternalStaff);
}
