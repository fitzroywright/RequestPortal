using RequestPortal.Domain.Messages;

namespace RequestPortal.Application.Messages;

public sealed record MailboxMessageView(
    Guid MessageId,
    Guid ThreadId,
    string Subject,
    string SenderName,
    string SenderKind,
    string Preview,
    DateTimeOffset SentUtc,
    DateTimeOffset? ReadUtc,
    Guid? OrganizationId,
    string? OrganizationName,
    Guid? RequestId,
    string? RequestReference);

public sealed record MessageThreadView(
    Guid ThreadId,
    string Subject,
    Guid? OrganizationId,
    string? OrganizationName,
    Guid? RequestId,
    string? RequestReference,
    MessageAudience Audience,
    IReadOnlyList<MessageDetailView> Messages);

public sealed record MessageAttachmentView(
    Guid DocumentId,
    string FileName,
    string ContentType,
    long SizeBytes);

public sealed record MessageDetailView(
    Guid MessageId,
    string SenderName,
    string? SenderEmail,
    string SenderKind,
    string Body,
    DateTimeOffset SentUtc,
    bool IsMine,
    IReadOnlyList<MessageAttachmentView> Attachments);

public sealed record MessageRecipientOption(
    Guid UserId,
    string DisplayName,
    bool IsStaff,
    bool IsOrganizationManager);

public sealed record PortalMessageCreated(
    Guid MessageId,
    Guid ThreadId,
    IReadOnlyCollection<Guid> RecipientUserIds,
    string Subject,
    string SenderName,
    string Preview);

public interface IPortalMessageRealtimeNotifier
{
    Task NotifyAsync(
        PortalMessageCreated message,
        CancellationToken cancellationToken = default);
}

public interface IPortalMessageService
{
    Task<int> GetUnreadCountAsync(
        Guid userId,
        bool isStaff,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailboxMessageView>> GetInboxAsync(
        Guid userId,
        bool isStaff,
        int take = 100,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailboxMessageView>> GetSentAsync(
        Guid userId,
        bool isStaff,
        int take = 100,
        CancellationToken cancellationToken = default);

    Task<MessageThreadView?> GetThreadAsync(
        Guid userId,
        bool isStaff,
        Guid threadId,
        CancellationToken cancellationToken = default);

    Task<Guid> SendAsync(
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
        IReadOnlyCollection<Guid>? attachmentDocumentIds = null);

    Task<IReadOnlyList<MessageRecipientOption>> GetOrganizationRecipientsAsync(
        Guid userId,
        bool isStaff,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<Guid> SendToSharedStaffAsync(
        Guid senderUserId,
        Guid organizationId,
        Guid? requestId,
        Guid? threadId,
        string subject,
        string body,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? attachmentDocumentIds = null);

    Task<Guid> ReplyAsync(
        Guid senderUserId,
        bool senderIsStaff,
        Guid threadId,
        string body,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<Guid>? attachmentDocumentIds = null);

    Task<Guid> SendSystemMessageAsync(
        Guid? organizationId,
        Guid? requestId,
        Guid? threadId,
        string subject,
        string body,
        IReadOnlyCollection<Guid> recipientUserIds,
        CancellationToken cancellationToken = default);

    Task<bool> CanAccessAttachmentAsync(
        Guid userId,
        bool isStaff,
        Guid threadId,
        Guid documentId,
        CancellationToken cancellationToken = default);

    Task MarkThreadReadAsync(
        Guid userId,
        Guid threadId,
        CancellationToken cancellationToken = default);
}
