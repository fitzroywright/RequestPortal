using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RequestPortal.Domain.Integration;

namespace RequestPortal.Application.Integration;

public sealed record IntegrationStatusSummary(
    Guid LinkId,
    Guid RequestId,
    string PublicReference,
    string OrganizationName,
    string SystemName,
    SynchronizationStatus Status,
    string? ExternalReference,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? SynchronizedUtc,
    string? LastError);

public interface IIntegrationStatusService
{
    Task<Guid> EnsureLinkAsync(Guid requestId, string systemName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntegrationStatusSummary>> GetAllAsync(CancellationToken cancellationToken = default);
    Task MarkPendingAsync(Guid linkId, CancellationToken cancellationToken = default);
    Task MarkSynchronizedAsync(Guid linkId, string externalReference, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(Guid linkId, string error, CancellationToken cancellationToken = default);
    Task RetryAsync(Guid linkId, CancellationToken cancellationToken = default);
    Task ReconcileAsync(Guid linkId, CancellationToken cancellationToken = default);
}
