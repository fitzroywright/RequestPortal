using System;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Auditing;

public interface IBusinessAuditWriter
{
    Task WriteAsync(
        Guid actorUserId,
        string action,
        string entityType,
        Guid entityId,
        Guid? organizationId,
        string? detail = null,
        CancellationToken cancellationToken = default);
}
