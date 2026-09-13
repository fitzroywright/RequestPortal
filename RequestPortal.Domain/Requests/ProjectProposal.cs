using System;

namespace RequestPortal.Domain.Requests;

public enum RequestKind
{
    GoodsAndServices = 0,
    Project = 1
}

public sealed class ProjectProposal
{
    private ProjectProposal()
    {
    }

    public ProjectProposal(Guid id, Guid requestId, Guid createdByUserId, DateTimeOffset createdUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Proposal id is required.", nameof(id));
        }

        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("Request id is required.", nameof(requestId));
        }

        Id = id;
        RequestId = requestId;
        CreatedByUserId = createdByUserId;
        LastEditedByUserId = createdByUserId;
        CreatedUtc = createdUtc;
        UpdatedUtc = createdUtc;
        Version = 1;
        Title = "Project Proposal";
        RichContentHtml = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid RequestId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string RichContentHtml { get; private set; } = string.Empty;

    public Guid CreatedByUserId { get; private set; }

    public Guid LastEditedByUserId { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset UpdatedUtc { get; private set; }

    public int Version { get; private set; }

    public void Update(string title, string richContentHtml, Guid editedByUserId, DateTimeOffset updatedUtc)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Proposal title is required.", nameof(title));
        }

        Title = title.Trim();
        RichContentHtml = richContentHtml?.Trim() ?? string.Empty;
        LastEditedByUserId = editedByUserId;
        UpdatedUtc = updatedUtc;
        Version++;
    }
}
