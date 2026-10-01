namespace RequestPortal.Domain.Messages;

public enum MessageAudience
{
    Individual = 0,
    Organization = 1,
    OrganizationManager = 2,
    FfpInternal = 3,
    PersonalNote = 4
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

    public Guid? AssignedToStaffUserId { get; private set; }

    public DateTimeOffset? AssignedUtc { get; private set; }

    public DateTimeOffset? ClosedUtc { get; private set; }

    public void Assign(Guid staffUserId, DateTimeOffset assignedUtc)
    {
        if (!StaffVisible)
        {
            throw new InvalidOperationException("Only staff-visible conversations can be assigned.");
        }
        if (ClosedUtc.HasValue)
        {
            throw new InvalidOperationException("Closed conversations cannot be assigned.");
        }

        AssignedToStaffUserId = staffUserId;
        AssignedUtc = assignedUtc;
    }

    public void Unassign()
    {
        AssignedToStaffUserId = null;
        AssignedUtc = null;
    }

    public void Close(DateTimeOffset closedUtc) => ClosedUtc = closedUtc;

    public void Reopen()
    {
        ClosedUtc = null;
    }
}
