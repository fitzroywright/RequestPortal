namespace RequestPortal.Domain.Messages;

public enum MessageAudience
{
    Individual = 0,
    Organization = 1,
    OrganizationManager = 2,
    FfpInternal = 3
}

public sealed class MessageThread
{
    private MessageThread() { }

    public MessageThread(
        Guid id,
        Guid? organizationId,
        Guid? requestId,
        string subject,
        bool staffVisible,
        DateTimeOffset createdUtc,
        MessageAudience audience = MessageAudience.Individual)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("Message subject is required.", nameof(subject));
        }

        Id = id;
        OrganizationId = organizationId;
        RequestId = requestId;
        Subject = subject.Trim();
        StaffVisible = staffVisible;
        Audience = audience;
        CreatedUtc = createdUtc;
    }

    public Guid Id { get; private set; }

    public Guid? OrganizationId { get; private set; }

    public Guid? RequestId { get; private set; }

    public string Subject { get; private set; } = string.Empty;

    public bool StaffVisible { get; private set; }

    public MessageAudience Audience { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset? ClosedUtc { get; private set; }

    public void Close(DateTimeOffset closedUtc) => ClosedUtc = closedUtc;
}
