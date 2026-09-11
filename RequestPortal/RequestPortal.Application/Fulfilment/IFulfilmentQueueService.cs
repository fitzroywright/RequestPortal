using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Fulfilment;

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
}
