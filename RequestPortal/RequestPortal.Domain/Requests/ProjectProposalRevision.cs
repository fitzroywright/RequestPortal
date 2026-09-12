namespace RequestPortal.Domain.Requests;

public sealed class ProjectProposalRevision
{
    private ProjectProposalRevision() { }

    public ProjectProposalRevision(
        Guid id,
        Guid proposalId,
        Guid requestId,
        int version,
        string title,
        string richContentHtml,
        Guid editedByUserId,
        DateTimeOffset savedUtc,
        bool automatic)
    {
        if (id == Guid.Empty) throw new ArgumentException("Revision id is required.", nameof(id));
        if (proposalId == Guid.Empty) throw new ArgumentException("Proposal id is required.", nameof(proposalId));
        if (requestId == Guid.Empty) throw new ArgumentException("Request id is required.", nameof(requestId));

        Id = id;
        ProposalId = proposalId;
        RequestId = requestId;
        Version = version;
        Title = title;
        RichContentHtml = richContentHtml;
        EditedByUserId = editedByUserId;
        SavedUtc = savedUtc;
        Automatic = automatic;
    }

    public Guid Id { get; private set; }
    public Guid ProposalId { get; private set; }
    public Guid RequestId { get; private set; }
    public int Version { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string RichContentHtml { get; private set; } = string.Empty;
    public Guid EditedByUserId { get; private set; }
    public DateTimeOffset SavedUtc { get; private set; }
    public bool Automatic { get; private set; }
}
