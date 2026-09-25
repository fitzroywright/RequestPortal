namespace RequestPortal.Domain.Messages;

public sealed class MessageRecipient
{
    private MessageRecipient() { }

    public MessageRecipient(Guid messageId, Guid userId)
    {
        MessageId = messageId;
        UserId = userId;
    }

    public Guid MessageId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset? ReadUtc { get; private set; }

    public DateTimeOffset? ArchivedUtc { get; private set; }

    public void MarkRead(DateTimeOffset readUtc)
    {
        ReadUtc ??= readUtc;
    }

    public void Archive(DateTimeOffset archivedUtc)
    {
        ArchivedUtc = archivedUtc;
    }
}
