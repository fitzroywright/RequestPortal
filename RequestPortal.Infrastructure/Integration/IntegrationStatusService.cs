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

    // Read-only candidate snapshot. Limited to the FFP Manager integration and
    // durable failures specifically recorded during the Artifacts stage.
    public async Task<IReadOnlyList<Guid>> GetFailedArtifactRecoveryIdsAsync(CancellationToken cancellationToken = default)
        => await dbContext.IntegrationLinks.AsNoTracking()
            .Where(link => link.SystemName == ffpManagerOptions.SystemName &&
                link.Status == SynchronizationStatus.Failed && link.FailureStage == "Artifacts")
            .OrderBy(link => link.CreatedUtc).Select(link => link.RequestId)
            .Take(1000).ToListAsync(cancellationToken);

    public async Task<bool> RecoverApplicationWithoutArtifactsAsync(Guid staffUserId, Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await operatorAuthorization.DemandAsync(staffUserId, cancellationToken);
        await using var requestLock = await IntegrationRequestLock.AcquireAsync(dbContext, requestId, cancellationToken);
        var link = await dbContext.IntegrationLinks.SingleOrDefaultAsync(
            item => item.RequestId == requestId && item.SystemName == ffpManagerOptions.SystemName, cancellationToken);
        // Idempotent: never touch successful, pending, or other failed transfers.
        if (link is null || link.Status != SynchronizationStatus.Failed || link.FailureStage != "Artifacts")
            return false;
        EnsureSupported(link);
        // A missing/ambiguous destination is never inserted or treated as synchronized.
        var receipt = await bridgeClient.FindApplicationAsync(requestId, cancellationToken);
        if (receipt is null) throw new InvalidOperationException("No confirmed FFP Manager application exists.");
        FfpManagerIntegrationService.ValidateReceipt(requestId, receipt);
        if (link.ExternalReference is not null && link.ExternalReference != receipt.ApplicationId ||
            link.ApplicationNumber is not null && link.ApplicationNumber != receipt.ApplicationNumber)
            throw new IntegrationTransferException(TransferFailures.For("DestinationVerification", "DESTINATION_CONFLICT"));
        var portalReference = await dbContext.AssistanceRequests.AsNoTracking()
            .Where(item => item.Id == requestId).Select(item => item.PublicReference)
            .SingleAsync(cancellationToken);
        if (string.Equals(portalReference, receipt.ApplicationNumber, StringComparison.OrdinalIgnoreCase))
            throw new IntegrationTransferException(TransferFailures.For("DestinationVerification", "DESTINATION_CONFLICT"));
        await IntegrationLegacyHistory.PreserveAsync(dbContext, link, cancellationToken);
        await auditWriter.WriteAsync(staffUserId, "ArtifactTransferBypassRecoveryRequested", "IntegrationLink", link.Id, null,
            $"request={requestId}; target=JMDB01/FFPJM; documents=skipped; reason=one-time-artifacts-recovery", cancellationToken);
        link.RecordVerifiedDestination(receipt.ApplicationId, receipt.ApplicationNumber, receipt.Server, receipt.Database, clock.UtcNow);
        link.MarkApplicationSynchronizedWithoutArtifacts(receipt.ApplicationId, receipt.ApplicationNumber, clock.UtcNow);
        var attempt = new IntegrationAttempt(link.Id, link.AttemptCount, clock.UtcNow, "ApplicationOnlyRecovery");
        attempt.SetStage("DestinationVerification");
        attempt.Complete(clock.UtcNow);
        dbContext.IntegrationAttempts.Add(attempt);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(staffUserId, "ArtifactTransferBypassRecoveryCompleted", "IntegrationLink", link.Id, null,
            $"request={requestId}; application={receipt.ApplicationNumber}; documents=skipped; no-upload=true", cancellationToken);
        return true;
    }

    private static readonly HashSet<string> CorrectableCodes = new(StringComparer.Ordinal)
    {
        "VALIDATION_PRIMARY_TELEPHONE", "VALIDATION_SECONDARY_TELEPHONE",
        "VALIDATION_DATE_OF_BIRTH", "VALIDATION_APPLICANT_EMAIL",
        "VALIDATION_EMPLOYMENT_STATUS", "VALIDATION_PARISH"
    };

    public async Task<QuickIntakeCorrection?> GetQuickIntakeCorrectionAsync(Guid staffUserId, Guid requestId, CancellationToken cancellationToken = default)
    {
        await operatorAuthorization.DemandAsync(staffUserId, cancellationToken);
        var link = await dbContext.IntegrationLinks.AsNoTracking()
            .SingleOrDefaultAsync(l => l.RequestId == requestId && l.SystemName == ffpManagerOptions.SystemName, cancellationToken);
        if (link is null || link.Status != SynchronizationStatus.Failed || !CorrectableCodes.Contains(link.ErrorCode ?? ""))
            return null;
        var intake = await dbContext.QuickIntakes.AsNoTracking().SingleOrDefaultAsync(q => q.RequestId == requestId, cancellationToken);
        if (intake is null) return null;
        var reference = await dbContext.AssistanceRequests.AsNoTracking().Where(r => r.Id == requestId)
            .Select(r => r.PublicReference).SingleAsync(cancellationToken);
        string field = TransferFailures.For(link.FailureStage ?? "Validation", link.ErrorCode!).Field
            ?? throw new InvalidOperationException("The rejected field cannot be identified.");
        string current = field switch
        {
            "PrimaryTelephone" => intake.MobileNumber,
            "SecondaryTelephone" => intake.SecondaryTelephone ?? "",
            "DateOfBirth" => intake.DateOfBirth.ToString("yyyy-MM-dd"),
            "ApplicantEmail" => intake.Email ?? "",
            "ApplicantEmploymentStatus" => intake.EmploymentStatus,
            "Parish" => intake.Parish,
            _ => throw new InvalidOperationException("Unsupported correction field.")
        };
        return new(requestId, reference, link.ErrorCode!, field, current);
    }

    public async Task CorrectQuickIntakeAsync(Guid staffUserId, Guid requestId, string errorCode, string? correctedValue,
        CancellationToken cancellationToken = default)
    {
        await operatorAuthorization.DemandAsync(staffUserId, cancellationToken);
        if (!CorrectableCodes.Contains(errorCode) || (correctedValue?.Length ?? 0) > 320)
            throw new ArgumentException("Invalid correction.");
        await using var requestLock = await IntegrationRequestLock.AcquireAsync(dbContext, requestId, cancellationToken);
        var link = await dbContext.IntegrationLinks.SingleOrDefaultAsync(
            l => l.RequestId == requestId && l.SystemName == ffpManagerOptions.SystemName, cancellationToken);
        if (link is null || link.Status != SynchronizationStatus.Failed || link.ErrorCode != errorCode)
            throw new InvalidOperationException("The recorded failure changed. Refresh diagnostics before correcting.");
        // Prevent edits when the destination is already known to exist: use the verified field-preview workflow instead.
        if (link.VerifiedUtc.HasValue || !string.IsNullOrEmpty(link.ExternalReference))
            throw new InvalidOperationException("A verified destination exists. Use the existing destination field-preview workflow.");
        var intake = await dbContext.QuickIntakes.SingleAsync(q => q.RequestId == requestId, cancellationToken);
        string field = TransferFailures.For(link.FailureStage ?? "Validation", errorCode).Field
            ?? throw new InvalidOperationException("Unknown validation field.");
        string? value = correctedValue?.Trim();
        switch (field)
        {
            case "PrimaryTelephone":
                value = ExternalApplicantValidation.Telephone(value, field);
                if (value is null) throw new ArgumentException("Primary telephone cannot be empty.");
                break;
            case "SecondaryTelephone": value = ExternalApplicantValidation.Telephone(value, field); break;
            case "ApplicantEmail": ExternalApplicantValidation.Email(value); break;
            case "DateOfBirth":
                if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var date))
                    throw new ArgumentException("Enter a valid date in YYYY-MM-DD format.");
                ExternalApplicantValidation.DateOfBirth(date);
                break;
            case "ApplicantEmploymentStatus":
                if (string.IsNullOrWhiteSpace(value) || value.Length > 50)
                    throw new ArgumentException("Employment status is required (maximum 50 characters).");
                ExternalApplicantValidation.Employment(value);
                break;
            case "Parish":
                if (string.IsNullOrWhiteSpace(value) || RequestPortal.Application.Management.JamaicaParishes.Normalize(value)
                        == RequestPortal.Application.Management.JamaicaParishes.Unknown)
                    throw new ArgumentException("Select a recognized Jamaican parish.");
                value = RequestPortal.Application.Management.JamaicaParishes.Normalize(value);
                break;
        }
        intake.CorrectTransferField(field, value);
        // Audit only metadata, never source personal data or submitted values.
        await auditWriter.WriteAsync(staffUserId, "QuickIntakeTransferFieldCorrected", "IntegrationLink", link.Id, null,
            $"field={field}; errorCode={errorCode}; request={requestId}; noRetry=true", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
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
