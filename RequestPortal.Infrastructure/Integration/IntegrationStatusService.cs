using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Integration;
using RequestPortal.Common.Time;
using RequestPortal.Common.Messaging;
using System.Text.Json;
using RequestPortal.Domain.Integration;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Integration;

public sealed partial class IntegrationStatusService : IIntegrationStatusService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IClock clock;
    private readonly IExternalApplicationIntegrationService ffpManagerIntegrationService;
    private readonly FfpManagerBridgeOptions ffpManagerOptions;
    private readonly IBusinessAuditWriter auditWriter;
    private readonly IExternalApplicationBridgeClient bridgeClient;
    private readonly IIntegrationOperatorAuthorization operatorAuthorization;

    public IntegrationStatusService(
        RequestPortalDbContext dbContext,
        IClock clock,
        IExternalApplicationIntegrationService ffpManagerIntegrationService,
        IOptions<FfpManagerBridgeOptions> ffpManagerOptions,
        IBusinessAuditWriter auditWriter,
        IExternalApplicationBridgeClient bridgeClient,
        IIntegrationOperatorAuthorization operatorAuthorization)
    {
        this.dbContext = dbContext;
        this.clock = clock;
        this.ffpManagerIntegrationService = ffpManagerIntegrationService;
        this.ffpManagerOptions = ffpManagerOptions.Value;
        this.auditWriter = auditWriter;
        this.bridgeClient = bridgeClient;
        this.operatorAuthorization = operatorAuthorization;
    }

    public async Task<Guid> EnsureLinkAsync(Guid requestId, string systemName, CancellationToken cancellationToken = default)
    {
        IntegrationLink? existing = await dbContext.IntegrationLinks.SingleOrDefaultAsync(item => item.RequestId == requestId && item.SystemName == systemName, cancellationToken);
        if (existing is not null) return existing.Id;
        bool requestExists = await dbContext.AssistanceRequests.AsNoTracking().AnyAsync(item => item.Id == requestId, cancellationToken);
        if (!requestExists) throw new InvalidOperationException("The request does not exist.");
        IntegrationLink link = new(Guid.NewGuid(), requestId, systemName, clock.UtcNow);
        dbContext.IntegrationLinks.Add(link);
        await dbContext.SaveChangesAsync(cancellationToken);
        return link.Id;
    }

    public async Task<IReadOnlyList<IntegrationStatusSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await (from link in dbContext.IntegrationLinks.AsNoTracking()
                      join request in dbContext.AssistanceRequests.AsNoTracking() on link.RequestId equals request.Id
                      join organization in dbContext.Organizations.AsNoTracking() on request.OrganizationId equals organization.Id
                      orderby request.CreatedUtc descending
                      select new IntegrationStatusSummary(link.Id, request.Id, request.PublicReference, organization.Name, link.SystemName, link.Status, link.ExternalReference, link.LastAttemptUtc, link.SynchronizedUtc, link.LastError))
            .ToListAsync(cancellationToken);
    }

    public async Task MarkPendingAsync(Guid linkId, CancellationToken cancellationToken = default)
    {
        IntegrationLink link = await GetLinkAsync(linkId, cancellationToken);
        await IntegrationLegacyHistory.PreserveAsync(dbContext,link,cancellationToken);
        link.MarkPending(clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkSynchronizedAsync(Guid linkId, string externalReference, CancellationToken cancellationToken = default)
    {
        IntegrationLink link = await GetLinkAsync(linkId, cancellationToken);
        await IntegrationLegacyHistory.PreserveAsync(dbContext,link,cancellationToken);
        if (link.VerifiedUtc >= clock.UtcNow.AddMinutes(-5) && link.ArtifactsSynchronizedUtc.HasValue && link.ExternalReference == externalReference)
            link.MarkSynchronized(externalReference, clock.UtcNow);
        else
            link.RequireReconciliation(clock.UtcNow, "ARTIFACT", "Destination verification or document acknowledgement is incomplete. Use an authorized safe retry.");
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(Guid linkId, string error, CancellationToken cancellationToken = default)
    {
        await RecordFailureAsync(linkId, TransferFailures.For("Reconciliation", "UNCLASSIFIED"), cancellationToken);
    }

    public async Task RecordFailureAsync(Guid linkId, TransferFailure failure, CancellationToken cancellationToken = default)
    {
        var link = await GetLinkAsync(linkId, cancellationToken);
        await IntegrationLegacyHistory.PreserveAsync(dbContext,link,cancellationToken);
        var attempt = new IntegrationAttempt(link.Id, link.AttemptCount, clock.UtcNow, "StatusRefresh");
        attempt.Fail(clock.UtcNow, failure.Stage, failure.Classification, failure.Code, failure.Explanation);
        dbContext.IntegrationAttempts.Add(attempt);
        link.RecordFailure(clock.UtcNow, failure.Stage, failure.Classification, failure.Code, failure.Explanation, false);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RetryAsync(Guid staffUserId, Guid linkId, CancellationToken cancellationToken = default)
    {
        await operatorAuthorization.DemandAsync(staffUserId, cancellationToken);
        IntegrationLink link = await GetLinkAsync(linkId, cancellationToken);
        EnsureSupported(link);
        await auditWriter.WriteAsync(staffUserId, "IntegrationRetryRequested", "IntegrationLink", link.Id, null,
            "Operator requested destination verification and a safe retry.", cancellationToken);
        // Reconciliation is read-only at the destination. An ambiguous result blocks the write.
        await ReconcileRequestAsync(staffUserId, link.RequestId, cancellationToken);
        if (await dbContext.QuickIntakes.AsNoTracking().AnyAsync(q => q.RequestId == link.RequestId, cancellationToken))
        {
            // Recheck before touching the submission queue. Existing Quick Intake applications
            // go to an explicit field preview, never an implicit historical overwrite.
            var destination = await bridgeClient.FindApplicationAsync(link.RequestId, cancellationToken);
            if (destination is not null)
            {
                FfpManagerIntegrationService.ValidateReceipt(link.RequestId, destination);
                if (destination.ApplicationId != link.ExternalReference || destination.ApplicationNumber != link.ApplicationNumber)
                    throw new IntegrationTransferException(TransferFailures.For("DestinationVerification", "DESTINATION_CONFLICT"));
                throw new ExistingDestinationPreviewRequiredException(link.RequestId);
            }
        }
        var queueId = FfpManagerSubmissionRequestedMessage.IdForRequest(link.RequestId);
        var queue = await dbContext.OutboxMessages.SingleOrDefaultAsync(item => item.Id == queueId, cancellationToken);
        if (queue is null)
        {
            var message = new FfpManagerSubmissionRequestedMessage(queueId, clock.UtcNow, link.RequestId);
            queue = new OutboxMessage { Id = queueId, RequestId = link.RequestId, OccurredUtc = message.OccurredUtc,
                MessageType = typeof(FfpManagerSubmissionRequestedMessage).FullName!, Payload = JsonSerializer.Serialize(message) };
            dbContext.OutboxMessages.Add(queue);
        }
        queue.ProcessedUtc = null; queue.AttemptCount = 0; queue.NextAttemptUtc = null; queue.LastError = null; queue.IsTerminal = false;
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            await ffpManagerIntegrationService.SubmitRequestAsync(link.RequestId, cancellationToken);
            queue.ProcessedUtc = clock.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditWriter.WriteAsync(staffUserId, "IntegrationRetried", "IntegrationLink", link.Id, null, link.SystemName, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = IntegrationFailureClassifier.Classify(exception, "Submission");
            queue.AttemptCount = 1;
            queue.IsTerminal = !failure.Transient || !link.NextRetryUtc.HasValue;
            queue.NextAttemptUtc = link.NextRetryUtc;
            queue.LastError = failure.Explanation;
            if(queue.IsTerminal) queue.StopRetrying(failure.Explanation);
            using var persistenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await dbContext.SaveChangesAsync(persistenceTimeout.Token);
            await auditWriter.WriteAsync(
                staffUserId,
                "IntegrationRetryFailed",
                "IntegrationLink",
                link.Id,
                null,
                $"system={link.SystemName}; failureType={exception.GetType().Name}",
                cancellationToken);
            throw;
        }
    }

    public async Task ReconcileAsync(Guid staffUserId, Guid linkId, CancellationToken cancellationToken = default)
    {
        await operatorAuthorization.DemandAsync(staffUserId, cancellationToken);
        IntegrationLink link = await GetLinkAsync(linkId, cancellationToken);
        await ReconcileRequestAsync(staffUserId, link.RequestId, cancellationToken);
    }

    public async Task ReconcileRequestAsync(Guid staffUserId, Guid requestId, CancellationToken cancellationToken = default)
    {
        await operatorAuthorization.DemandAsync(staffUserId, cancellationToken);
        await using var requestLock = await IntegrationRequestLock.AcquireAsync(dbContext, requestId, cancellationToken);
        var linkId = await EnsureLinkAsync(requestId, ffpManagerOptions.SystemName, cancellationToken);
        IntegrationLink link = await GetLinkAsync(linkId, cancellationToken);
        EnsureSupported(link);
        await auditWriter.WriteAsync(staffUserId, "IntegrationReconciliationRequested", "IntegrationLink", link.Id, null,
            "Read-only destination verification requested.", cancellationToken);
        var attempt = new IntegrationAttempt(link.Id, link.AttemptCount, clock.UtcNow, "Reconciliation");
        await IntegrationLegacyHistory.PreserveAsync(dbContext,link,cancellationToken);
        dbContext.IntegrationAttempts.Add(attempt);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var receipt = await bridgeClient.FindApplicationAsync(requestId, cancellationToken);
            if (receipt is null)
            {
                link.RequireReconciliation(clock.UtcNow, "DESTINATION_NOT_FOUND", "No active destination record matches this immutable request ID. An authorized retry can submit it.");
            }
            else
            {
                FfpManagerIntegrationService.ValidateReceipt(requestId, receipt);
                if (link.ExternalReference is not null && link.ExternalReference != receipt.ApplicationId)
                    throw new IntegrationTransferException(TransferFailures.For("Reconciliation", "DESTINATION_CONFLICT"));
                link.RecordVerifiedDestination(receipt.ApplicationId, receipt.ApplicationNumber, receipt.Server, receipt.Database, clock.UtcNow);
                // Reconciliation never claims that untransferred artifacts have succeeded.
                if (link.ArtifactsSynchronizedUtc.HasValue)
                    link.MarkSynchronized(receipt.ApplicationId, clock.UtcNow, receipt.ApplicationNumber);
                else
                    link.RequireReconciliation(clock.UtcNow, "ARTIFACT", "Destination record verified. Document transfer and acknowledgement must complete through a safe retry.");
            }
            attempt.SetStage("DestinationLookup");
            attempt.Complete(clock.UtcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
            await auditWriter.WriteAsync(staffUserId, "IntegrationReconciled", "IntegrationLink", link.Id, null,
                receipt is null ? "DestinationNotFound" : "DestinationVerified", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failure = IntegrationFailureClassifier.Classify(exception, "Reconciliation");
            attempt.Fail(clock.UtcNow, failure.Stage, failure.Classification, failure.Code, failure.Explanation);
            string? bridgeDetails = exception is IntegrationTransferException transfer ? transfer.Failure.TechnicalDetails : null;
            attempt.SetExceptionDetails(IntegrationExceptionDiagnostics.IsSafeTransportDetail(bridgeDetails)
                ? bridgeDetails
                : IntegrationExceptionDiagnostics.Capture(exception, "Reconciliation", failure.Code));
            link.RecordFailure(clock.UtcNow, failure.Stage, failure.Classification, failure.Code, failure.Explanation, false);
            using var persistenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await dbContext.SaveChangesAsync(persistenceTimeout.Token);
            await auditWriter.WriteAsync(
                staffUserId,
                "IntegrationReconciliationFailed",
                "IntegrationLink",
                link.Id,
                null,
                $"system={link.SystemName}; failureType={exception.GetType().Name}",
                cancellationToken);
            throw new IntegrationTransferException(failure);
        }
    }

    private void EnsureSupported(IntegrationLink link)
    {
        if (!ffpManagerOptions.Enabled || !string.Equals(link.SystemName, ffpManagerOptions.SystemName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Integration '{link.SystemName}' is disabled or has no commissioned replay/reconciliation adapter.");
        }
    }

    private Task<IntegrationLink> GetLinkAsync(Guid linkId, CancellationToken cancellationToken) => dbContext.IntegrationLinks.SingleAsync(item => item.Id == linkId, cancellationToken);

    private static Guid CreateDeterministicMessageId(string externalReference, string statusCode, DateTimeOffset changedUtc)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{externalReference}|{statusCode}|{changedUtc:O}"));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }
}
