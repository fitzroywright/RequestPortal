using System;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Domain.Fulfilment;

namespace RequestPortal.Application.Requests;

public interface IRequestLifecycleService
{
    Task CancelAsync(Guid userId, Guid requestId, string reason, CancellationToken cancellationToken = default);

    Task MarkDuplicateAsync(Guid staffUserId, Guid requestId, string reason, CancellationToken cancellationToken = default);

    Task<Guid> StartFulfilmentAsync(
        Guid staffUserId,
        Guid requestId,
        FulfilmentSource source,
        CancellationToken cancellationToken = default);

    Task CompleteFulfilmentAsync(
        Guid staffUserId,
        Guid fulfilmentId,
        string? notes,
        CancellationToken cancellationToken = default);
}
