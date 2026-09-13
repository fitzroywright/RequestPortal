using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Fulfilment;

public sealed record FulfilmentLineView(
    Guid LineId,
    Guid RequestItemId,
    string Description,
    decimal RequestedQuantity,
    decimal FulfilledQuantity,
    decimal RemainingQuantity,
    decimal FulfilledValue,
    string? Notes);

public sealed record FulfilmentQueueItem(
    Guid RequestId,
    string PublicReference,
    Guid OrganizationId,
    string OrganizationName,
    string RequestStatus,
    string? Summary,
    Guid? FulfilmentId,
    string? FulfilmentSource,
    string? FulfilmentStatus,
    string? ExternalReference,
    DateTimeOffset CreatedUtc);

public interface IFulfilmentQueueService
{
    Task<IReadOnlyList<FulfilmentQueueItem>> GetQueueAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FulfilmentLineView>> GetLinesAsync(Guid requestId, CancellationToken cancellationToken = default);
    Task<Guid> StartAsync(Guid staffUserId, Guid requestId, string source, string? externalReference, CancellationToken cancellationToken = default);
    Task RecordProgressAsync(Guid staffUserId, Guid requestId, Guid requestItemId, decimal quantity, decimal value, string? notes, CancellationToken cancellationToken = default);
    Task CompleteAsync(Guid staffUserId, Guid requestId, string? notes, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid staffUserId, Guid requestId, string reason, CancellationToken cancellationToken = default);
}
