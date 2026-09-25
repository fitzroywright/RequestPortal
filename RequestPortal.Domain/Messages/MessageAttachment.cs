namespace RequestPortal.Domain.Messages;

public sealed class MessageAttachment
{
    private MessageAttachment() { }

    public MessageAttachment(Guid messageId, Guid documentId)
    {
        if (messageId == Guid.Empty)
        {
            throw new ArgumentException("Message id is required.", nameof(messageId));
        }

        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document id is required.", nameof(documentId));
        }

        MessageId = messageId;
        DocumentId = documentId;
    }

    public Guid MessageId { get; private set; }

    public Guid DocumentId { get; private set; }
}
