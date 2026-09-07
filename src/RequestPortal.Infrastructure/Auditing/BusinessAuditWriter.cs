using System;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Application.Auditing;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Auditing;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Auditing;

public sealed class BusinessAuditWriter : IBusinessAuditWriter
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IClock clock;

    public BusinessAuditWriter(RequestPortalDbContext dbContext, IClock clock)
    {
        this.dbContext = dbContext;
        this.clock = clock;
    }

    public async Task WriteAsync(
        Guid actorUserId,
        string action,
        string entityType,
        Guid entityId,
        Guid? organizationId,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        AuditEntry entry = new(
            Guid.NewGuid(),
            actorUserId,
            action,
            entityType,
            entityId,
            organizationId,
            clock.UtcNow,
            detail);

        dbContext.AuditEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
