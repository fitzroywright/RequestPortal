namespace RequestPortal.Domain.Messages;

public enum PortalMessageSenderKind
{
    Representative = 0,
    FfpStaff = 1,
    System = 2
}

public sealed class PortalMessage
{
    private PortalMessage() { }

    public PortalMessage(
        Guid id,
        Guid threadId,
        Guid? senderUserId,
        PortalMessageSenderKind senderKind,
        string senderDisplayName,
        string body,
        DateTimeOffset sentUtc)
    {
        if (string.IsNullOrWhiteSpace(senderDisplayName))
        {
            throw new ArgumentException("Sender display name is required.", nameof(senderDisplayName));
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("Message body is required.", nameof(body));
        }

        Id = id;
        ThreadId = threadId;
        SenderUserId = senderUserId;
        SenderKind = senderKind;
        SenderDisplayName = senderDisplayName.Trim();
        Body = body.Trim();
        SentUtc = sentUtc;
    }

    public Guid Id { get; private set; }

    public Guid ThreadId { get; private set; }

    public Guid? SenderUserId { get; private set; }

    public PortalMessageSenderKind SenderKind { get; private set; }

    public string SenderDisplayName { get; private set; } = string.Empty;

    public string Body { get; private set; } = string.Empty;

    public DateTimeOffset SentUtc { get; private set; }
}
