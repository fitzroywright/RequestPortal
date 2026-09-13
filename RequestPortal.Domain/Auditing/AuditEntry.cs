using System;

namespace RequestPortal.Domain.Auditing;

public sealed class AuditEntry
{
    private AuditEntry()
    {
    }

    public AuditEntry(
        Guid id,
        Guid actorUserId,
        string action,
        string entityType,
        Guid entityId,
        Guid? organizationId,
        DateTimeOffset occurredUtc,
        string? detail = null)
    {
        Id = id;
        ActorUserId = actorUserId;
        Action = action.Trim();
        EntityType = entityType.Trim();
        EntityId = entityId;
        OrganizationId = organizationId;
        OccurredUtc = occurredUtc;
        Detail = detail;
    }

    public Guid Id { get; private set; }

    public Guid ActorUserId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public Guid EntityId { get; private set; }

    public Guid? OrganizationId { get; private set; }

    public DateTimeOffset OccurredUtc { get; private set; }

    public string? Detail { get; private set; }
}
