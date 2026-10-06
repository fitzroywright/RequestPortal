using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Messages;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Auditing;
using RequestPortal.Domain.Messages;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Messages;

public sealed class PortalMessageService : IPortalMessageService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IPortalMessageRealtimeNotifier realtimeNotifier;
    private readonly IExternalApplicationBridgeClient bridgeClient;
    private readonly FfpManagerBridgeOptions bridgeOptions;
    private readonly IBusinessAuditWriter? auditWriter;

    public PortalMessageService(
        RequestPortalDbContext dbContext,
        IPortalMessageRealtimeNotifier realtimeNotifier,
        IExternalApplicationBridgeClient bridgeClient,
        IOptions<FfpManagerBridgeOptions> bridgeOptions,
        IBusinessAuditWriter? auditWriter = null)
    {
        this.dbContext = dbContext;
        this.realtimeNotifier = realtimeNotifier;
        this.bridgeClient = bridgeClient;
        this.bridgeOptions = bridgeOptions.Value;
        this.auditWriter = auditWriter;
    }

    public async Task<int> GetUnreadCountAsync(
        Guid userId,
        bool isStaff,
        CancellationToken cancellationToken = default)
    {
        if (isStaff)
        {
            await EnsureSharedStaffMailboxRecipientsAsync(
                userId,
                cancellationToken);
        }

        return await (
            from recipient in dbContext.MessageRecipients.AsNoTracking()
            join message in dbContext.PortalMessages.AsNoTracking()
                on recipient.MessageId equals message.Id
            join thread in dbContext.MessageThreads.AsNoTracking()
                on message.ThreadId equals thread.Id
            where recipient.UserId == userId &&
                  recipient.ReadUtc == null &&
                  recipient.ArchivedUtc == null &&
                  thread.ClosedUtc == null
            select recipient)
            .CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MailboxMessageView>> GetInboxAsync(
        Guid userId,
        bool isStaff,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (isStaff)
        {
            await EnsureSharedStaffMailboxRecipientsAsync(
                userId,
                cancellationToken);
        }

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
            where recipient.UserId == userId &&
                  recipient.ArchivedUtc == null &&
                  thread.ClosedUtc == null
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
                isStaff,
                dbContext.MessageAttachments.Any(attachment => attachment.MessageId == row.Message.Id)))
            .ToArray();
    }

    public async Task<IReadOnlyList<MailboxMessageView>> GetAssignedToMeAsync(
        Guid staffUserId,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        await EnsureInternalStaffAsync(staffUserId, cancellationToken);

        Guid[] threadIds = await dbContext.MessageThreads
            .AsNoTracking()
            .Where(thread =>
                thread.StaffVisible &&
                thread.AssignedToStaffUserId == staffUserId &&
                thread.ClosedUtc == null)
            .Select(thread => thread.Id)
            .ToArrayAsync(cancellationToken);

        return await GetStaffQueueAsync(
            staffUserId,
            threadIds,
            take,
            cancellationToken);
    }

    public async Task<IReadOnlyList<MailboxMessageView>> GetUnassignedAsync(
        Guid staffUserId,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        await EnsureInternalStaffAsync(staffUserId, cancellationToken);

        Guid[] threadIds = await dbContext.MessageThreads
            .AsNoTracking()
            .Where(thread =>
                thread.StaffVisible &&
                thread.AssignedToStaffUserId == null &&
                thread.ClosedUtc == null)
            .Select(thread => thread.Id)
            .ToArrayAsync(cancellationToken);

        return await GetStaffQueueAsync(
            staffUserId,
            threadIds,
            take,
            cancellationToken);
    }

    public async Task<IReadOnlyList<MailboxMessageView>> GetArchivedAsync(
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
            join recipient in dbContext.MessageRecipients.AsNoTracking()
                on message.Id equals recipient.MessageId into recipients
            join sender in dbContext.Users.AsNoTracking()
                on message.SenderUserId equals (Guid?)sender.Id into senders
            from sender in senders.DefaultIfEmpty()
            join organization in dbContext.Organizations.AsNoTracking()
                on thread.OrganizationId equals organization.Id into organizations
            from organization in organizations.DefaultIfEmpty()
            join request in dbContext.AssistanceRequests.AsNoTracking()
                on thread.RequestId equals request.Id into requests
            from request in requests.DefaultIfEmpty()
            where thread.ClosedUtc != null &&
                  (isStaff
                      ? thread.StaffVisible
                      : message.SenderUserId == userId || recipients.Any(item => item.UserId == userId))
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

        var messageIds = rows.Select(row => row.Message.Id).ToArray();
        var attachmentMessageIds = await dbContext.MessageAttachments.AsNoTracking()
            .Where(item => messageIds.Contains(item.MessageId))
            .Select(item => item.MessageId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var attachmentSet = attachmentMessageIds.ToHashSet();

        return rows
            .GroupBy(row => row.Thread.Id)
            .Select(group => group
                .OrderByDescending(row => row.Message.SentUtc)
                .First())
            .Select(row => ToMailboxView(
                row.Message,
                row.Thread,
                null,
                row.Sender,
                row.OrganizationName,
                row.RequestReference,
                isStaff,
                attachmentSet.Contains(row.Message.Id)))
            .OrderByDescending(message => message.SentUtc)
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
            where thread.ClosedUtc == null &&
                  (isStaff
                    ? message.SenderKind == PortalMessageSenderKind.FfpStaff
                    : message.SenderUserId == userId)
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
                isStaff,
                dbContext.MessageAttachments.Any(attachment => attachment.MessageId == row.Message.Id)))
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

        if (isStaff && thread.StaffVisible)
        {
            await EnsureSharedStaffThreadRecipientsAsync(
                userId,
                threadId,
                cancellationToken);
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

        Guid[] messageIds = messages
            .Select(row => row.Message.Id)
            .ToArray();

        var attachmentRows = await (
            from attachment in dbContext.MessageAttachments.AsNoTracking()
            join document in dbContext.Documents.AsNoTracking()
                on attachment.DocumentId equals document.Id into documents
            from document in documents.DefaultIfEmpty()
            where messageIds.Contains(attachment.MessageId)
            select new
            {
                attachment.MessageId,
                DocumentId = attachment.DocumentId,
                FileName = document == null ? "Document unavailable" : document.OriginalFileName,
                ContentType = document == null ? string.Empty : document.ContentType,
                SizeBytes = document == null ? 0 : document.SizeBytes,
                IsAvailable = document != null && !document.IsArchived
            })
            .ToListAsync(cancellationToken);

        Dictionary<Guid, MessageAttachmentView[]> attachmentsByMessage = attachmentRows
            .GroupBy(row => row.MessageId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(row => new MessageAttachmentView(
                        row.DocumentId,
                        row.FileName,
                        row.ContentType,
                        row.SizeBytes,
                        row.IsAvailable))
                    .ToArray());

        MessageDetailView[] projected = messages
            .Select(row =>
            {
                (string senderName, string senderKind, string? senderEmail) =
                    ProjectSender(row.Message, row.Sender, isStaff);
                IReadOnlyList<MessageAttachmentView> attachments =
                    attachmentsByMessage.TryGetValue(row.Message.Id, out MessageAttachmentView[]? values)
                        ? values
                        : Array.Empty<MessageAttachmentView>();

                return new MessageDetailView(
                    row.Message.Id,
                    row.Message.SenderUserId,
                    senderName,
                    senderKind,
                    senderEmail,
                    row.Message.Body,
                    row.Message.SentUtc,
                    row.Message.SenderUserId == userId,
                    attachments);
            })
            .ToArray();

        string? assignedToStaffName = null;
        if (thread.AssignedToStaffUserId.HasValue)
        {
            var assignedUser = await dbContext.Users
                .AsNoTracking()
                .Where(user => user.Id == thread.AssignedToStaffUserId.Value)
                .Select(user => new { user.DisplayName, user.Email })
                .SingleOrDefaultAsync(cancellationToken);

            if (assignedUser is not null)
            {
                assignedToStaffName = FirstName(
                    assignedUser.DisplayName,
                    assignedUser.Email);
            }
        }

        return new MessageThreadView(
            thread.Id,
            thread.Subject,
            thread.OrganizationId,
            organizationName,
            thread.RequestId,
            requestReference,
            thread.Audience,
            thread.AssignedToStaffUserId,
            assignedToStaffName,
            thread.AssignedUtc,
            thread.ClosedUtc,
            projected);
    }

    public async Task<IReadOnlyList<MessageThreadView>> GetRequestThreadsAsync(
        Guid userId,
        bool isStaff,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        Guid[] threadIds = await dbContext.MessageThreads
            .AsNoTracking()
            .Where(thread => thread.RequestId == requestId)
            .OrderBy(thread => thread.CreatedUtc)
            .Select(thread => thread.Id)
            .ToArrayAsync(cancellationToken);

        List<MessageThreadView> threads = [];
        foreach (Guid threadId in threadIds)
        {
            MessageThreadView? thread = await GetThreadAsync(
                userId,
                isStaff,
                threadId,
                cancellationToken);
            if (thread is not null)
            {
                threads.Add(thread);
            }
        }

        return threads;
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
                user.IsInternalStaff,
                membership.Role
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new MessageRecipientOption(
                row.Id,
                FirstName(row.DisplayName, row.Email),
                null,
                row.IsInternalStaff,
                row.Role == OrganizationRole.Administrator))
            .ToArray();
    }

    public async Task<Guid> SavePersonalNoteAsync(
        Guid senderUserId,
        Guid organizationId,
        Guid requestId,
        Guid? threadId,
        string subject,
        string body,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? attachmentDocumentIds = null)
    {
        await EnsureApprovedMembershipAsync(
            senderUserId,
            organizationId,
            cancellationToken);
        await ValidateRequestScopeAsync(
            organizationId,
            requestId,
            cancellationToken);

        return await SendInternalAsync(
            senderUserId,
            PortalMessageSenderKind.Representative,
            organizationId,
            requestId,
            threadId,
            subject,
            body,
            Array.Empty<Guid>(),
            staffVisible: false,
            cancellationToken,
            attachmentDocumentIds,
            MessageAudience.PersonalNote);
    }

    public async Task<Guid> SendToSharedStaffAsync(
        Guid senderUserId,
        Guid organizationId,
        Guid? requestId,
        Guid? threadId,
        string subject,
        string body,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? attachmentDocumentIds = null,
        Guid? stableMessageId = null)
    {
        await EnsureApprovedMembershipAsync(senderUserId, organizationId, cancellationToken);
        await ValidateRequestScopeAsync(organizationId, requestId, cancellationToken);

        if (requestId.HasValue && !threadId.HasValue)
        {
            // Access to a request conversation follows the same organization-membership
            // boundary as Request Details. EnsureApprovedMembershipAsync and
            // ValidateRequestScopeAsync above already prove that the representative
            // may access this organization's request. Notification subscription is
            // a delivery preference, not an additional send-permission gate.
            MessageThread? canonical = await dbContext.MessageThreads.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == requestId.Value, cancellationToken);
            if (canonical?.ClosedUtc is not null)
                throw new InvalidOperationException("This conversation is closed. Contact FFP to reopen it.");
        }

        if (threadId.HasValue)
        {
            MessageThread thread = await dbContext.MessageThreads.SingleAsync(item => item.Id == threadId.Value, cancellationToken);
            if (!thread.StaffVisible || thread.Audience == MessageAudience.FfpInternal ||
                !await CanViewThreadAsync(senderUserId, false, thread, cancellationToken))
                throw new UnauthorizedAccessException("The current user cannot use this FFP conversation.");
            if (thread.ClosedUtc.HasValue)
                throw new InvalidOperationException("This conversation is closed. Contact FFP to reopen it.");
        }

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
            cancellationToken,
            attachmentDocumentIds,
            audience: requestId.HasValue ? MessageAudience.Request : MessageAudience.Individual,
            stableMessageId: stableMessageId);
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
        CancellationToken cancellationToken = default,
        MessageAudience audience = MessageAudience.Individual,
        IReadOnlyCollection<Guid>? attachmentDocumentIds = null)
    {
        if (recipientUserIds.Contains(senderUserId))
        {
            throw new InvalidOperationException("You cannot send a message to yourself.");
        }

        if (audience == MessageAudience.Individual && recipientUserIds.Count == 0)
        {
            throw new ArgumentException("An individual recipient is required for an individual conversation.", nameof(recipientUserIds));
        }

        if (!senderIsStaff)
        {
            if (!organizationId.HasValue)
            {
                throw new InvalidOperationException("Representative messages must belong to an organization.");
            }

            await EnsureApprovedMembershipAsync(senderUserId, organizationId.Value, cancellationToken);

            if (audience == MessageAudience.Organization)
            {
                await EnsureOrganizationAdministratorAsync(
                    senderUserId,
                    organizationId.Value,
                    cancellationToken);
            }

            await EnsureRepresentativeRecipientsAsync(
                senderUserId,
                organizationId.Value,
                recipientUserIds,
                cancellationToken);
        }

        await ValidateRequestScopeAsync(organizationId, requestId, cancellationToken);

        if (audience == MessageAudience.FfpInternal && !senderIsStaff)
        {
            throw new UnauthorizedAccessException("Only FFP staff can create internal conversations.");
        }

        Guid[] effectiveRecipients = recipientUserIds.Distinct().ToArray();
        if (organizationId.HasValue && audience == MessageAudience.Organization)
        {
            effectiveRecipients = await dbContext.OrganizationMemberships.AsNoTracking()
                .Where(membership =>
                    membership.OrganizationId == organizationId.Value &&
                    membership.Status == MembershipStatus.Approved &&
                    membership.UserId != senderUserId)
                .Select(membership => membership.UserId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
        }
        else if (organizationId.HasValue && audience == MessageAudience.OrganizationManager)
        {
            effectiveRecipients = await dbContext.OrganizationMemberships.AsNoTracking()
                .Where(membership =>
                    membership.OrganizationId == organizationId.Value &&
                    membership.Status == MembershipStatus.Approved &&
                    membership.Role == OrganizationRole.Administrator &&
                    membership.UserId != senderUserId)
                .Select(membership => membership.UserId)
                .Distinct()
                .ToArrayAsync(cancellationToken);
        }
        else if (audience == MessageAudience.FfpInternal)
        {
            effectiveRecipients = await dbContext.Users.AsNoTracking()
                .Where(user => user.IsInternalStaff && user.Id != senderUserId)
                .Select(user => user.Id)
                .ToArrayAsync(cancellationToken);
        }

        if (effectiveRecipients.Length == 0)
        {
            throw new InvalidOperationException("No eligible recipients are available for the selected audience.");
        }

        bool staffVisible =
            senderIsStaff ||
            audience == MessageAudience.FfpInternal ||
            await dbContext.Users.AsNoTracking()
                .AnyAsync(
                    user =>
                        effectiveRecipients.Contains(user.Id) &&
                        user.IsInternalStaff,
                    cancellationToken);

        return await SendInternalAsync(
            senderUserId,
            senderIsStaff ? PortalMessageSenderKind.FfpStaff : PortalMessageSenderKind.Representative,
            organizationId,
            requestId,
            threadId,
            subject,
            body,
            effectiveRecipients,
            staffVisible,
            cancellationToken,
            attachmentDocumentIds,
            audience);
    }

    public async Task<Guid> ReplyAsync(
        Guid senderUserId,
        bool senderIsStaff,
        Guid threadId,
        string body,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? attachmentDocumentIds = null)
    {
        MessageThread thread = await dbContext.MessageThreads
            .SingleAsync(item => item.Id == threadId, cancellationToken);

        if (thread.ClosedUtc.HasValue)
        {
            throw new InvalidOperationException("This conversation is closed. Reopen it before replying.");
        }

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
        if (thread.Audience == MessageAudience.Request && thread.RequestId.HasValue)
        {
            var request = await dbContext.AssistanceRequests.AsNoTracking()
                .Where(item => item.Id == thread.RequestId.Value)
                .Select(item => new { item.CreatedByUserId, item.SubmittedByUserId })
                .SingleAsync(cancellationToken);
            Guid[] managers = await dbContext.OrganizationMemberships.AsNoTracking()
                .Where(item => item.OrganizationId == thread.OrganizationId &&
                    item.Status == MembershipStatus.Approved && item.Role == OrganizationRole.Administrator &&
                    item.ReceiveRequestNotifications)
                .Select(item => item.UserId).ToArrayAsync(cancellationToken);
            Guid[] authors = await dbContext.PortalMessages.AsNoTracking()
                .Where(item => item.ThreadId == threadId && item.SenderUserId != null)
                .Select(item => item.SenderUserId!.Value).Distinct().ToArrayAsync(cancellationToken);
            Guid[] staff = await dbContext.Users.AsNoTracking().Where(item => item.IsInternalStaff)
                .Select(item => item.Id).ToArrayAsync(cancellationToken);
            recipients = managers.Concat(authors).Concat(staff)
                .Append(request.CreatedByUserId).Append(request.SubmittedByUserId ?? request.CreatedByUserId)
                .Where(id => id != senderUserId).Distinct().ToArray();
        }
        else if (thread.Audience == MessageAudience.FfpInternal)
        {
            if (!senderIsStaff)
            {
                throw new UnauthorizedAccessException("Only FFP staff can reply to internal conversations.");
            }

            recipients = await dbContext.Users
                .AsNoTracking()
                .Where(user => user.IsInternalStaff && user.Id != senderUserId)
                .Select(user => user.Id)
                .ToArrayAsync(cancellationToken);
        }
        else if (senderIsStaff)
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

        if (thread.Audience != MessageAudience.PersonalNote &&
            recipients.Length == 0)
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
            cancellationToken,
            attachmentDocumentIds,
            thread.Audience);
    }

    public async Task<Guid> SendStaffAlertAsync(
        Guid? organizationId,
        Guid? requestId,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        await ValidateRequestScopeAsync(
            organizationId,
            requestId,
            cancellationToken);

        MessageThread thread = await ResolveThreadAsync(
            threadId: null,
            organizationId,
            requestId,
            subject,
            staffVisible: true,
            cancellationToken,
            MessageAudience.FfpInternal);

        Guid[] staffUserIds = await dbContext.Users
            .AsNoTracking()
            .Where(user => user.IsInternalStaff)
            .Select(user => user.Id)
            .ToArrayAsync(cancellationToken);

        if (staffUserIds.Length == 0)
        {
            throw new InvalidOperationException("No FFP staff mailbox identities are available.");
        }

        PortalMessage message = new(
            Guid.NewGuid(),
            thread.Id,
            senderUserId: null,
            PortalMessageSenderKind.System,
            "Request Portal",
            body,
            DateTimeOffset.UtcNow);

        dbContext.PortalMessages.Add(message);
        AddRecipients(
            message.Id,
            staffUserIds,
            senderUserId: null);
        await dbContext.SaveChangesAsync(cancellationToken);

        await NotifyAsync(
            message,
            thread,
            staffUserIds,
            cancellationToken);

        return thread.Id;
    }

    public async Task<Guid> SendSystemMessageAsync(
        Guid? organizationId,
        Guid? requestId,
        Guid? threadId,
        string subject,
        string body,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken = default,
        Guid? stableMessageId = null,
        bool notifyExternally = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        if (recipientUserIds.Count == 0)
        {
            throw new ArgumentException("At least one message recipient is required.", nameof(recipientUserIds));
        }

        await ValidateRequestScopeAsync(organizationId, requestId, cancellationToken);

        Guid messageId = stableMessageId ?? Guid.NewGuid();
        Guid? deliveredThreadId = await dbContext.PortalMessages.AsNoTracking()
            .Where(item => item.Id == messageId).Select(item => (Guid?)item.ThreadId)
            .SingleOrDefaultAsync(cancellationToken);
        if (deliveredThreadId.HasValue) return deliveredThreadId.Value;

        MessageThread thread = await ResolveThreadAsync(
            threadId,
            organizationId,
            requestId,
            subject,
            staffVisible: true,
            cancellationToken,
            requestId.HasValue ? MessageAudience.Request : MessageAudience.Individual);

        PortalMessage message = new(
            messageId,
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
        if (notifyExternally)
            AddExternalNotificationOutbox(message, thread, allRecipients);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (auditWriter is not null)
        {
            await auditWriter.WriteAsync(
                Guid.Empty,
                "SystemMessageSent",
                "PortalMessage",
                message.Id,
                thread.OrganizationId,
                $"thread={thread.Id:D}; request={thread.RequestId}; recipients={allRecipients.Length}",
                cancellationToken);
        }

        await NotifyAsync(message, thread, allRecipients, cancellationToken);
        return thread.Id;
    }

    public async Task<bool> CanAccessAttachmentAsync(
        Guid userId,
        bool isStaff,
        Guid threadId,
        Guid documentId,
        CancellationToken cancellationToken = default)
    {
        MessageThread? thread = await dbContext.MessageThreads
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == threadId, cancellationToken);

        if (thread is null || !await CanViewThreadAsync(userId, isStaff, thread, cancellationToken))
        {
            return false;
        }

        return await (
            from attachment in dbContext.MessageAttachments.AsNoTracking()
            join message in dbContext.PortalMessages.AsNoTracking()
                on attachment.MessageId equals message.Id
            where message.ThreadId == threadId &&
                  attachment.DocumentId == documentId
            select attachment)
            .AnyAsync(cancellationToken);
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
            if (auditWriter is not null)
            {
                await auditWriter.WriteAsync(
                userId,
                "MessageThreadRead",
                "MessageThread",
                threadId,
                null,
                $"Unread messages marked read={recipients.Count}",
                cancellationToken);
            }
        }
    }

    public async Task AssignThreadAsync(
        Guid staffUserId,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        MessageThread thread = await LoadStaffThreadForMutationAsync(
            staffUserId,
            threadId,
            cancellationToken);

        thread.Assign(staffUserId, DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (auditWriter is not null)
        {
            await auditWriter.WriteAsync(
                staffUserId,
                "MessageThreadAssigned",
                "MessageThread",
                threadId,
                thread.OrganizationId,
                $"assignedTo={staffUserId:D}",
                cancellationToken);
        }
    }

    public async Task UnassignThreadAsync(
        Guid staffUserId,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        MessageThread thread = await LoadStaffThreadForMutationAsync(
            staffUserId,
            threadId,
            cancellationToken);

        thread.Unassign();
        await dbContext.SaveChangesAsync(cancellationToken);

        if (auditWriter is not null)
        {
            await auditWriter.WriteAsync(
                staffUserId,
                "MessageThreadUnassigned",
                "MessageThread",
                threadId,
                thread.OrganizationId,
                null,
                cancellationToken);
        }
    }

    public async Task CloseThreadAsync(
        Guid staffUserId,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        MessageThread thread = await LoadStaffThreadForMutationAsync(
            staffUserId,
            threadId,
            cancellationToken);

        thread.Close(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (auditWriter is not null)
        {
            await auditWriter.WriteAsync(
                staffUserId,
                "MessageThreadClosed",
                "MessageThread",
                threadId,
                thread.OrganizationId,
                null,
                cancellationToken);
        }
    }

    public async Task ReopenThreadAsync(
        Guid staffUserId,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInternalStaffAsync(staffUserId, cancellationToken);

        MessageThread thread = await dbContext.MessageThreads
            .SingleAsync(
                item => item.Id == threadId && item.StaffVisible,
                cancellationToken);

        thread.Reopen();
        await dbContext.SaveChangesAsync(cancellationToken);

        if (auditWriter is not null)
        {
            await auditWriter.WriteAsync(
                staffUserId,
                "MessageThreadReopened",
                "MessageThread",
                threadId,
                thread.OrganizationId,
                null,
                cancellationToken);
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
        CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? attachmentDocumentIds = null,
        MessageAudience audience = MessageAudience.Individual,
        Guid? stableMessageId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
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
            cancellationToken,
            audience);

        Guid messageId = stableMessageId ?? Guid.NewGuid();
        if (await dbContext.PortalMessages.AsNoTracking().AnyAsync(item => item.Id == messageId, cancellationToken))
        {
            return thread.Id;
        }

        PortalMessage message = new(
            messageId,
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

        Guid[] attachments = attachmentDocumentIds?
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray() ?? Array.Empty<Guid>();
        if (attachments.Length > 0)
        {
            await ValidateAttachmentDocumentsAsync(
                attachments,
                organizationId,
                requestId,
                cancellationToken);
            foreach (Guid documentId in attachments)
            {
                dbContext.MessageAttachments.Add(
                    new MessageAttachment(message.Id, documentId));
            }
        }

        if (audience != MessageAudience.PersonalNote)
        {
            AddExternalNotificationOutbox(
                message,
                thread,
                recipients);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (senderKind == PortalMessageSenderKind.Representative &&
            staffVisible &&
            requestId.HasValue &&
            audience == MessageAudience.Request)
        {
            await MirrorApplicantMessageToFfpManagerAsync(
                message.Id,
                requestId.Value,
                senderUserId,
                message.SenderDisplayName,
                body,
                cancellationToken);
        }

        if (auditWriter is not null)
        {
            await auditWriter.WriteAsync(
            senderUserId,
            "MessageSent",
            "PortalMessage",
            message.Id,
            thread.OrganizationId,
            $"thread={thread.Id:D}; request={thread.RequestId}; recipients={recipients.Length}; attachments={attachments.Length}",
            cancellationToken);
        }

        if (audience != MessageAudience.PersonalNote)
        {
            await NotifyAsync(
                message,
                thread,
                recipients,
                cancellationToken);
        }

        return thread.Id;
    }

    private async Task MirrorApplicantMessageToFfpManagerAsync(
        Guid messageId,
        Guid requestId,
        Guid senderUserId,
        string senderName,
        string body,
        CancellationToken cancellationToken)
    {
        if (!bridgeOptions.Enabled)
        {
            return;
        }

        string? applicationId = await dbContext.IntegrationLinks
            .AsNoTracking()
            .Where(link =>
                link.RequestId == requestId &&
                link.SystemName == bridgeOptions.SystemName &&
                link.ExternalReference != null)
            .Select(link => link.ExternalReference)
            .SingleOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return;
        }

        string note = string.IsNullOrWhiteSpace(senderName)
            ? body
            : $"{senderName}: {body}";

        ExternalInformationResponse response = new(
            messageId,
            requestId,
            applicationId,
            senderUserId,
            note,
            DateTimeOffset.UtcNow,
            Array.Empty<ExternalArtifactDescriptor>());

        await bridgeClient.SendInformationResponseAsync(
            response,
            cancellationToken);
    }

    private async Task<MessageThread> ResolveThreadAsync(
        Guid? threadId,
        Guid? organizationId,
        Guid? requestId,
        string subject,
        bool staffVisible,
        CancellationToken cancellationToken,
        MessageAudience audience = MessageAudience.Individual)
    {
        // One public FFP/applicant conversation per request. Explicit direct and
        // restricted conversations keep their own IDs and are never folded into it.
        if (!threadId.HasValue && audience == MessageAudience.Request && requestId.HasValue)
        {
            threadId = requestId.Value;
            MessageThread? canonical = await dbContext.MessageThreads
                .SingleOrDefaultAsync(item => item.Id == threadId.Value, cancellationToken);
            if (canonical is not null)
            {
                if (canonical.RequestId != requestId || canonical.OrganizationId != organizationId || canonical.Audience != MessageAudience.Request)
                    throw new InvalidOperationException("The request conversation has an invalid scope.");
                return canonical;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string canonicalSubject = subject.Trim();
            string canonicalAudience = MessageAudience.Request.ToString();
            // Concurrent confirmation and reply operations converge on the same
            // primary key instead of creating two conversations or failing an insert.
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO message_threads ("Id", "OrganizationId", "RequestId", "Subject", "StaffVisible", "CreatedUtc", "Audience")
                VALUES ({requestId.Value}, {organizationId}, {requestId}, {canonicalSubject}, {staffVisible}, {now}, {canonicalAudience})
                ON CONFLICT ("Id") DO NOTHING
                """, cancellationToken);
            MessageThread resolved = await dbContext.MessageThreads.SingleAsync(item => item.Id == requestId.Value, cancellationToken);
            if (resolved.RequestId != requestId || resolved.OrganizationId != organizationId || resolved.Audience != MessageAudience.Request)
                throw new InvalidOperationException("The request conversation has an invalid scope.");
            return resolved;
        }

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
            DateTimeOffset.UtcNow,
            audience);
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

    private async Task EnsureSharedStaffMailboxRecipientsAsync(
        Guid staffUserId,
        CancellationToken cancellationToken)
    {
        await EnsureInternalStaffAsync(staffUserId, cancellationToken);

        Guid[] messageIds = await (
            from message in dbContext.PortalMessages.AsNoTracking()
            join thread in dbContext.MessageThreads.AsNoTracking()
                on message.ThreadId equals thread.Id
            where thread.StaffVisible &&
                  thread.ClosedUtc == null &&
                  message.SenderUserId != staffUserId &&
                  (message.SenderKind != PortalMessageSenderKind.FfpStaff ||
                   thread.Audience == MessageAudience.FfpInternal)
            select message.Id)
            .ToArrayAsync(cancellationToken);

        if (messageIds.Length == 0)
        {
            return;
        }

        Guid[] existing = await dbContext.MessageRecipients
            .AsNoTracking()
            .Where(recipient =>
                recipient.UserId == staffUserId &&
                messageIds.Contains(recipient.MessageId))
            .Select(recipient => recipient.MessageId)
            .ToArrayAsync(cancellationToken);

        HashSet<Guid> existingSet = existing.ToHashSet();
        foreach (Guid messageId in messageIds.Where(id => !existingSet.Contains(id)))
        {
            dbContext.MessageRecipients.Add(
                new MessageRecipient(messageId, staffUserId));
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task EnsureSharedStaffThreadRecipientsAsync(
        Guid staffUserId,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        await EnsureInternalStaffAsync(staffUserId, cancellationToken);

        Guid[] messageIds = await (
            from message in dbContext.PortalMessages.AsNoTracking()
            join thread in dbContext.MessageThreads.AsNoTracking()
                on message.ThreadId equals thread.Id
            where message.ThreadId == threadId &&
                  message.SenderUserId != staffUserId &&
                  (message.SenderKind != PortalMessageSenderKind.FfpStaff ||
                   thread.Audience == MessageAudience.FfpInternal)
            select message.Id)
            .ToArrayAsync(cancellationToken);

        Guid[] existing = await dbContext.MessageRecipients
            .AsNoTracking()
            .Where(recipient =>
                recipient.UserId == staffUserId &&
                messageIds.Contains(recipient.MessageId))
            .Select(recipient => recipient.MessageId)
            .ToArrayAsync(cancellationToken);

        HashSet<Guid> existingSet = existing.ToHashSet();
        foreach (Guid messageId in messageIds.Where(id => !existingSet.Contains(id)))
        {
            dbContext.MessageRecipients.Add(
                new MessageRecipient(messageId, staffUserId));
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyList<MailboxMessageView>> GetStaffQueueAsync(
        Guid staffUserId,
        IReadOnlyCollection<Guid> threadIds,
        int take,
        CancellationToken cancellationToken)
    {
        if (threadIds.Count == 0)
        {
            return Array.Empty<MailboxMessageView>();
        }

        IReadOnlyList<MailboxMessageView> inbox = await GetInboxAsync(
            staffUserId,
            isStaff: true,
            Math.Clamp(take * 5, take, 250),
            cancellationToken);

        HashSet<Guid> allowed = threadIds.ToHashSet();

        return inbox
            .Where(message => allowed.Contains(message.ThreadId))
            .GroupBy(message => message.ThreadId)
            .Select(group => group.OrderByDescending(message => message.SentUtc).First())
            .OrderByDescending(message => message.SentUtc)
            .Take(Math.Clamp(take, 1, 250))
            .ToArray();
    }

    private async Task<MessageThread> LoadStaffThreadForMutationAsync(
        Guid staffUserId,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        await EnsureInternalStaffAsync(staffUserId, cancellationToken);

        return await dbContext.MessageThreads
            .SingleAsync(
                item =>
                    item.Id == threadId &&
                    item.StaffVisible &&
                    item.ClosedUtc == null,
                cancellationToken);
    }

    private async Task EnsureInternalStaffAsync(
        Guid staffUserId,
        CancellationToken cancellationToken)
    {
        bool isStaff = await dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                user =>
                    user.Id == staffUserId &&
                    user.IsInternalStaff,
                cancellationToken);

        if (!isStaff)
        {
            throw new UnauthorizedAccessException(
                "FFP staff access is required for shared mailbox operations.");
        }
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

    private async Task EnsureOrganizationAdministratorAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        bool administrator = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(
                membership =>
                    membership.UserId == userId &&
                    membership.OrganizationId == organizationId &&
                    membership.Status == MembershipStatus.Approved &&
                    membership.Role == OrganizationRole.Administrator,
                cancellationToken);

        if (!administrator)
        {
            throw new UnauthorizedAccessException(
                "Organization administrator access is required for organization-wide messages.");
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

    private async Task ValidateAttachmentDocumentsAsync(
        IReadOnlyCollection<Guid> documentIds,
        Guid? organizationId,
        Guid? requestId,
        CancellationToken cancellationToken)
    {
        if (!organizationId.HasValue)
        {
            throw new InvalidOperationException("Attachments require an organization-scoped conversation.");
        }

        int validDocuments = await dbContext.Documents
            .AsNoTracking()
            .CountAsync(
                document =>
                    documentIds.Contains(document.Id) &&
                    !document.IsArchived &&
                    document.OrganizationId == organizationId.Value &&
                    (!requestId.HasValue ||
                     document.OwnerType == RequestPortal.Domain.Documents.DocumentOwnerType.Organization ||
                     (document.OwnerType == RequestPortal.Domain.Documents.DocumentOwnerType.AssistanceRequest &&
                      document.OwnerId == requestId.Value)),
                cancellationToken);

        if (validDocuments != documentIds.Count)
        {
            throw new UnauthorizedAccessException("One or more attachments are outside the conversation scope.");
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
        bool viewerIsStaff,
        bool hasAttachments = false)
    {
        (string senderName, string senderKind, string? senderEmail) =
            ProjectSender(message, sender, viewerIsStaff);
        string preview = message.Body.Length <= 140
            ? message.Body
            : message.Body[..137] + "...";

        return new MailboxMessageView(
            message.Id,
            message.SenderUserId,
            thread.Id,
            thread.Subject,
            senderName,
            senderKind,
            senderEmail,
            preview,
            message.SentUtc,
            readUtc,
            thread.OrganizationId,
            organizationName,
            thread.RequestId,
            requestReference,
            hasAttachments);
    }

    private static (string Name, string Kind, string? Email) ProjectSender(
        PortalMessage message,
        ApplicationUser? sender,
        bool viewerIsStaff)
    {
        if (message.SenderKind == PortalMessageSenderKind.System)
        {
            return ("FFP Request Processing", "FFP", null);
        }

        string name = FirstName(
            sender?.DisplayName ?? message.SenderDisplayName,
            sender?.Email);

        if (message.SenderKind == PortalMessageSenderKind.FfpStaff)
        {
            return (name, "FFP Staff", null);
        }

        return (
            name,
            "Representative",
            null);
    }

    private static string FirstName(string? displayName, string? email)
    {
        string candidate = string.IsNullOrWhiteSpace(displayName)
            ? email?.Split('@')[0] ?? "User"
            : displayName.Trim();

        if (candidate.Contains('@'))
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
