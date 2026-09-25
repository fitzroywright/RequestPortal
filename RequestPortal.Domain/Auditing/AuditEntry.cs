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
        string? detail = null,
        string applicationContext = "RequestPortal",
        string outcome = "Succeeded")
    {
        Id = id;
        ActorUserId = actorUserId;
        Action = action.Trim();
        EntityType = entityType.Trim();
        EntityId = entityId;
        OrganizationId = organizationId;
        OccurredUtc = occurredUtc;
        Detail = detail;
        ApplicationContext = applicationContext.Trim();
        Outcome = outcome.Trim();
    }

    public Guid Id { get; private set; }

    public Guid ActorUserId { get; private set; }

    public string Action { get; private set; } = string.Empty;

    public string EntityType { get; private set; } = string.Empty;

    public Guid EntityId { get; private set; }

    public Guid? OrganizationId { get; private set; }

    public DateTimeOffset OccurredUtc { get; private set; }

    public string? Detail { get; private set; }

    public string ApplicationContext { get; private set; } = "RequestPortal";

    public string Outcome { get; private set; } = "Succeeded";
}
