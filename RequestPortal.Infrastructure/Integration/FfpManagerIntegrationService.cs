using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RequestPortal.Application.Documents;
using RequestPortal.Application.Integration;
using RequestPortal.Application.Messages;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Integration;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Integration;

public sealed class FfpManagerIntegrationService : IExternalApplicationIntegrationService
{
    private const string ConsumerName = "FfpManagerIntegration";
    private static readonly Guid ExternalSystemActorId = new("00000000-0000-0000-0000-00000000f001");

    private readonly RequestPortalDbContext dbContext;
    private readonly IDocumentService documentService;
    private readonly IExternalApplicationBridgeClient bridgeClient;
    private readonly FfpManagerBridgeOptions options;
    private readonly IClock clock;
    private readonly IPortalMessageService portalMessageService;
    private readonly ILogger<FfpManagerIntegrationService>? logger;

    public FfpManagerIntegrationService(
        RequestPortalDbContext dbContext,
        IDocumentService documentService,
        IExternalApplicationBridgeClient bridgeClient,
        IOptions<FfpManagerBridgeOptions> options,
        IClock clock,
        IPortalMessageService portalMessageService,
        ILogger<FfpManagerIntegrationService>? logger = null)
    {
        this.dbContext = dbContext;
        this.documentService = documentService;
        this.bridgeClient = bridgeClient;
        this.options = options.Value;
        this.clock = clock;
        this.portalMessageService = portalMessageService;
        this.logger = logger;
    }

    public async Task<ExternalApplicationReference> SubmitRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await using var requestLock = await IntegrationRequestLock.AcquireAsync(dbContext, requestId, cancellationToken);
        IntegrationLink link = await GetOrCreateLinkAsync(requestId, cancellationToken);
        await IntegrationLegacyHistory.PreserveAsync(dbContext,link,cancellationToken);
        link.BeginAttempt(clock.UtcNow);
        var attempt = new IntegrationAttempt(link.Id, link.AttemptCount, clock.UtcNow);
        dbContext.IntegrationAttempts.Add(attempt);
        await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
        EnsureEnabled();

        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.Items)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        if (request.Status == RequestStatus.Draft)
        {
            throw new InvalidOperationException("Draft requests cannot be sent to FFP Manager.");
        }

        var organization = await dbContext.Organizations
            .AsNoTracking()
            .Where(item => item.Id == request.OrganizationId)
            .Select(item => new
            {
                item.Id,
                item.Name,
                item.RegistrationNumber,
                item.Address,
                item.Phone,
                item.Email,
                item.OrganizationType,
                item.Parish
            })
            .SingleAsync(cancellationToken);

        // Quick Intake belongs to an individual. Use the captured applicant name
        // rather than the synthetic organization name in the FFP application fields.
        var quickIntakeApplicant = await dbContext.QuickIntakes
            .AsNoTracking()
            .Where(item => item.RequestId == request.Id)
            .Select(item => new { item.FirstName, item.LastName })
            .SingleOrDefaultAsync(cancellationToken);
        string applicationName = quickIntakeApplicant is null
            ? organization.Name
            : string.Join(" ", new[] { quickIntakeApplicant.FirstName, quickIntakeApplicant.LastName }
                .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        if (string.IsNullOrWhiteSpace(applicationName))
            throw new InvalidOperationException("Quick Intake applicant first and last name are required for FFP Manager.");

            Guid[] unitIds = request.Items
                .Select(item => item.UnitOfMeasureId)
                .Distinct()
                .ToArray();
            Dictionary<Guid, string> units = await dbContext.UnitsOfMeasure
                .AsNoTracking()
                .Where(unit => unitIds.Contains(unit.Id))
                .ToDictionaryAsync(unit => unit.Id, unit => unit.Name, cancellationToken);

            List<StoredDocument> documents = await dbContext.Documents
                .AsNoTracking()
                .Where(document =>
                    document.OwnerType == DocumentOwnerType.AssistanceRequest &&
                    document.OwnerId == request.Id &&
                    !document.IsArchived)
                .OrderBy(document => document.UploadedUtc)
                .ToListAsync(cancellationToken);

            List<ExternalApplicationItem> items = request.Items
                .Select(item => new ExternalApplicationItem(
                    item.Id,
                    item.Description,
                    item.Quantity,
                    units.TryGetValue(item.UnitOfMeasureId, out string? unit) ? unit : item.UnitOfMeasureId.ToString("D")))
                .ToList();

            List<ExternalArtifactDescriptor> artifacts = documents
                .Select(ToArtifactDescriptor)
                .ToList();

            var intake = await dbContext.QuickIntakes.AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == request.Id, cancellationToken);
            string? applicantParish = await dbContext.Users.AsNoTracking()
                .Where(item => item.Id == (request.SubmittedByUserId ?? request.CreatedByUserId))
                .Select(item => item.Parish).SingleOrDefaultAsync(cancellationToken);
            string parish = FfpManagerSubmissionParish.Resolve(
                request.PublicReference, intake, organization.OrganizationType, organization.Parish, applicantParish);
            ExternalApplicationSubmission submission = new(
                link.Id,
                request.Id,
                request.PublicReference,
                organization.Id,
                applicationName,
                organization.RegistrationNumber,
                organization.Address,
                organization.Phone,
                organization.Email,
                request.SubmittedByUserId ?? request.CreatedByUserId,
                request.SubmittedUtc ?? request.CreatedUtc,
                request.Kind.ToString(),
                request.Priority.ToString(),
                request.Summary,
                request.Category,
                request.Subcategory,
                request.ProgramCategory,
                request.Location,
                request.Circumstances,
                items,
                artifacts,
                parish,
                intake is null ? "Request Portal" : "Request Portal - Quick Intake",
                intake?.FirstName,
                intake?.LastName,
                intake?.StreetAddress,
                intake?.Email,
                intake?.Gender,
                intake?.EmploymentStatus,
                intake?.StreetAddress,
                intake?.CommunityDistrict,
                intake?.MobileNumber,
                intake?.SecondaryTelephone,
                intake?.DateOfBirth);

            attempt.SetStage("DestinationLookup");
            await dbContext.SaveChangesAsync(cancellationToken);
            var receipt = await bridgeClient.FindApplicationAsync(request.Id, cancellationToken);
            ExternalApplicationReference reference;
            if (receipt is null)
            {
                attempt.SetStage("SqlSubmission");
                await dbContext.SaveChangesAsync(cancellationToken);
                reference = await bridgeClient.SubmitApplicationAsync(submission, cancellationToken);
                attempt.SetStage("AcknowledgementVerification");
                await dbContext.SaveChangesAsync(cancellationToken);
                receipt = await bridgeClient.FindApplicationAsync(request.Id, cancellationToken);
                if (receipt is null || receipt.ApplicationId != reference.ApplicationId || receipt.ApplicationNumber != reference.ApplicationNumber)
                    throw new IntegrationTransferException(TransferFailures.For(attempt.Stage, "DESTINATION_CONFLICT"));
            }
            else
                reference = new(receipt.ApplicationId, receipt.ApplicationNumber, receipt.AcceptedUtc, AlreadyExisted: true);
            ValidateReceipt(request.Id, receipt);
            if (string.Equals(receipt.ApplicationNumber, request.PublicReference, StringComparison.OrdinalIgnoreCase))
                throw new IntegrationTransferException(TransferFailures.For("AcknowledgementVerification", "DESTINATION_CONFLICT"));
            link.RecordVerifiedDestination(receipt.ApplicationId, receipt.ApplicationNumber, receipt.Server, receipt.Database, clock.UtcNow);
            attempt.SetStage("Artifacts");
            // Persist the SQL commit identity before any document/notification failure can occur.
            await dbContext.SaveChangesAsync(cancellationToken);

            string? applicationNumber = reference.ApplicationNumber?.Trim();
            if (string.IsNullOrWhiteSpace(applicationNumber) ||
                string.Equals(applicationNumber, request.PublicReference, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("FFP Manager did not return a generated ReferenceNo. Notifications remain pending.");

            Guid documentAccessUserId = request.SubmittedByUserId ?? request.CreatedByUserId;
            foreach (StoredDocument document in documents)
            {
                DocumentDownload? download = await documentService.DownloadAsync(
                    documentAccessUserId,
                    document.Id,
                    isStaff: true,
                    cancellationToken);
                if (download is null)
                {
                    throw new InvalidOperationException($"Request artifact {document.Id:D} could not be opened for integration transfer.");
                }

                await using Stream content = download.Content;
                await bridgeClient.UploadArtifactAsync(
                    reference.ApplicationId,
                    ToArtifactDescriptor(document),
                    content,
                    cancellationToken);
            }

            link.MarkSynchronized(reference.ApplicationId, clock.UtcNow, applicationNumber);
            attempt.SetStage("Confirmation");
            Guid confirmationId = FfpManagerClientConfirmationRequestedMessage.IdForRequest(request.Id);
            if (!dbContext.OutboxMessages.Local.Any(item => item.Id == confirmationId) &&
                !await dbContext.OutboxMessages.AsNoTracking().AnyAsync(item => item.Id == confirmationId, cancellationToken))
                AddOutbox(new FfpManagerClientConfirmationRequestedMessage(confirmationId, clock.UtcNow, request.Id, applicationNumber));
            Guid emailId = QuickIntakeClientRelationsEmailRequestedMessage.IdForRequest(request.Id);
            OutboxMessage? email = await dbContext.OutboxMessages.SingleOrDefaultAsync(item => item.Id == emailId, cancellationToken);
            if (email is not null && email.ProcessedUtc is null &&
                email.MessageType == QuickIntakeClientRelationsEmailRequestedMessage.AwaitingReferenceMessageType)
            {
                var notification = JsonSerializer.Deserialize<QuickIntakeClientRelationsEmailRequestedMessage>(email.Payload)
                    ?? throw new InvalidOperationException("Invalid Client Relations outbox payload.");
                email.Payload = JsonSerializer.Serialize(notification with { FfpManagerReference = applicationNumber });
                email.MessageType = typeof(QuickIntakeClientRelationsEmailRequestedMessage).FullName!;
            }
            attempt.Complete(clock.UtcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
            return reference;
        }
        catch (Exception exception)
        {
            logger?.LogError(exception,
                "FFP Manager integration exception for request {RequestId}, link {LinkId}, attempt {AttemptNumber}, stage {Stage}",
                requestId, link.Id, attempt.Number, attempt.Stage);
            var failure = IntegrationFailureClassifier.Classify(exception, attempt.Stage);
            link.RecordFailure(clock.UtcNow, failure.Stage, failure.Classification, failure.Code, failure.Explanation, failure.Transient);
            attempt.Fail(clock.UtcNow, failure.Stage, failure.Classification, failure.Code, failure.Explanation);
            // Preserve bounded sanitized original exception chain separately from the
            // ordinary list-view explanation; do not persist exception.ToString().
            string? bridgeDetails = exception is IntegrationTransferException transfer
                ? transfer.Failure.TechnicalDetails : null;
            attempt.SetExceptionDetails(IntegrationExceptionDiagnostics.IsSafeTransportDetail(bridgeDetails)
                ? bridgeDetails
                : IntegrationExceptionDiagnostics.Capture(exception, attempt.Stage, failure.Code));
            // Caller cancellation must not erase the durable failure checkpoint.
            using var persistenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await dbContext.SaveChangesAsync(persistenceTimeout.Token);
            if(exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw new IntegrationTransferException(failure with { Transient = failure.Transient && link.NextRetryUtc.HasValue });
        }
    }

    public async Task<ExternalApplicationStatus?> RefreshStatusAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        IntegrationLink? link = await dbContext.IntegrationLinks
            .SingleOrDefaultAsync(
                item => item.RequestId == requestId && item.SystemName == options.SystemName,
                cancellationToken);

        if (link is null || string.IsNullOrWhiteSpace(link.ExternalReference))
        {
            return null;
        }

        var receipt = await bridgeClient.FindApplicationAsync(requestId, cancellationToken);
        if (receipt is null) throw new IntegrationTransferException(TransferFailures.For("Reconciliation", "DESTINATION_CONFLICT"));
        ValidateReceipt(requestId, receipt);
        if (receipt.ApplicationId != link.ExternalReference ||
            (!string.IsNullOrWhiteSpace(link.ApplicationNumber) && receipt.ApplicationNumber != link.ApplicationNumber))
            throw new IntegrationTransferException(TransferFailures.For("Reconciliation", "DESTINATION_CONFLICT"));
        link.RecordVerifiedDestination(receipt.ApplicationId, receipt.ApplicationNumber, receipt.Server, receipt.Database, clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await bridgeClient.GetStatusAsync(link.ExternalReference, cancellationToken);
    }

    public async Task ReceiveStatusAsync(
        Guid messageId,
        Guid requestId,
        ExternalApplicationStatus status,
        CancellationToken cancellationToken = default)
    {
        if (!await BeginInboxAsync(messageId, cancellationToken))
        {
            return;
        }

        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        RequestStatus originalStatus = request.Status;
        ApplyCanonicalStatus(request, status);
        if (request.Status != originalStatus)
        {
            AddStatusOutbox(request, status.ChangedUtc);
        }

        await CompleteInboxAsync(messageId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ReceiveInformationRequestAsync(
        ExternalInformationRequest informationRequest,
        CancellationToken cancellationToken = default)
    {
        if (!await BeginInboxAsync(informationRequest.MessageId, cancellationToken))
        {
            return;
        }

        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == informationRequest.PortalRequestId, cancellationToken);

        if (request.Status == RequestStatus.Submitted)
        {
            request.RequestMoreInformation(ExternalSystemActorId, informationRequest.RequestedUtc, informationRequest.Message);
        }

        Guid primaryRecipientUserId = request.SubmittedByUserId ?? request.CreatedByUserId;
        Guid[] subscribedManagerIds = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership =>
                membership.OrganizationId == request.OrganizationId &&
                membership.Status == MembershipStatus.Approved &&
                membership.Role == OrganizationRole.Administrator &&
                membership.ReceiveRequestNotifications)
            .Select(membership => membership.UserId)
            .ToArrayAsync(cancellationToken);

        Guid[] recipients = subscribedManagerIds
            .Append(primaryRecipientUserId)
            .Distinct()
            .ToArray();

        Guid? existingThreadId = await dbContext.MessageThreads
            .AsNoTracking()
            .Where(thread =>
                thread.RequestId == request.Id &&
                thread.StaffVisible &&
                thread.Audience == RequestPortal.Domain.Messages.MessageAudience.Request)
            .OrderByDescending(thread => thread.CreatedUtc)
            .Select(thread => (Guid?)thread.Id)
            .FirstOrDefaultAsync(cancellationToken);

        string body = informationRequest.Message;
        if (informationRequest.DueUtc.HasValue)
        {
            body += $"\n\nPlease respond by {informationRequest.DueUtc.Value:yyyy-MM-dd}.";
        }

        await portalMessageService.SendSystemMessageAsync(
            request.OrganizationId,
            request.Id,
            existingThreadId,
            $"Information required: {request.PublicReference}",
            body,
            recipients,
            cancellationToken,
            stableMessageId: informationRequest.MessageId,
            notifyExternally: false);

        FfpManagerInformationRequestedMessage message = new(
            Guid.NewGuid(),
            clock.UtcNow,
            request.Id,
            informationRequest.Message,
            informationRequest.DueUtc);
        AddOutbox(message);
        await CompleteInboxAsync(informationRequest.MessageId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RespondToInformationRequestAsync(
        Guid requestId,
        string applicationId,
        Guid userId,
        string message,
        IReadOnlyList<Guid> documentIds,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        if (string.IsNullOrWhiteSpace(message) && documentIds.Count == 0)
        {
            throw new ArgumentException("A response message or supporting document is required.", nameof(message));
        }

        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        List<StoredDocument> documents = await dbContext.Documents
            .AsNoTracking()
            .Where(document =>
                documentIds.Contains(document.Id) &&
                document.OwnerType == DocumentOwnerType.AssistanceRequest &&
                document.OwnerId == requestId &&
                !document.IsArchived)
            .ToListAsync(cancellationToken);

        if (documents.Count != documentIds.Distinct().Count())
        {
            throw new InvalidOperationException("One or more response documents do not belong to this request.");
        }

        List<ExternalArtifactDescriptor> artifacts = documents.Select(ToArtifactDescriptor).ToList();
        ExternalInformationResponse response = new(
            Guid.NewGuid(),
            requestId,
            applicationId,
            userId,
            message.Trim(),
            clock.UtcNow,
            artifacts);

        foreach (StoredDocument document in documents)
        {
            DocumentDownload? download = await documentService.DownloadAsync(userId, document.Id, isStaff: false, cancellationToken);
            if (download is null)
            {
                throw new InvalidOperationException($"Response artifact {document.Id:D} could not be opened.");
            }

            await using Stream content = download.Content;
            await bridgeClient.UploadArtifactAsync(applicationId, ToArtifactDescriptor(document), content, cancellationToken);
        }

        await bridgeClient.SendInformationResponseAsync(response, cancellationToken);

        Guid? existingThreadId = await dbContext.MessageThreads
            .AsNoTracking()
            .Where(thread =>
                thread.RequestId == request.Id &&
                thread.StaffVisible &&
                thread.Audience == RequestPortal.Domain.Messages.MessageAudience.Request)
            .OrderByDescending(thread => thread.CreatedUtc)
            .Select(thread => (Guid?)thread.Id)
            .FirstOrDefaultAsync(cancellationToken);

        await portalMessageService.SendToSharedStaffAsync(
            userId,
            request.OrganizationId,
            request.Id,
            existingThreadId,
            $"Information response: {request.PublicReference}",
            message,
            cancellationToken: cancellationToken,
            attachmentDocumentIds: documentIds,
            stableMessageId: response.MessageId);

        if (request.Status == RequestStatus.MoreInformationRequired)
        {
            DateTimeOffset now = clock.UtcNow;
            request.Resubmit(userId, now);
            AddStatusOutbox(request, now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public static void ValidateReceipt(Guid requestId, ExternalApplicationReceipt receipt)
    {
        if (receipt.PortalRequestId != requestId || string.IsNullOrWhiteSpace(receipt.ApplicationId) ||
            string.IsNullOrWhiteSpace(receipt.ApplicationNumber) ||
            !string.Equals(receipt.Server, "JMDB01", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(receipt.Database, "FFPJM", StringComparison.OrdinalIgnoreCase))
            throw new IntegrationTransferException(TransferFailures.For("DestinationVerification", "DESTINATION_CONFLICT"));
    }

    private async Task<IntegrationLink> GetOrCreateLinkAsync(Guid requestId, CancellationToken cancellationToken)
    {
        IntegrationLink? link = await dbContext.IntegrationLinks
            .SingleOrDefaultAsync(
                item => item.RequestId == requestId && item.SystemName == options.SystemName,
                cancellationToken);
        if (link is not null)
        {
            return link;
        }

        link = new IntegrationLink(Guid.NewGuid(), requestId, options.SystemName, clock.UtcNow);
        dbContext.IntegrationLinks.Add(link);
        await dbContext.SaveChangesAsync(cancellationToken);
        return link;
    }

    private async Task<bool> BeginInboxAsync(Guid messageId, CancellationToken cancellationToken)
    {
        InboxMessage? existing = await dbContext.InboxMessages
            .SingleOrDefaultAsync(
                item => item.MessageId == messageId && item.Consumer == ConsumerName,
                cancellationToken);
        if (existing is not null)
        {
            return existing.ProcessedUtc is null;
        }

        dbContext.InboxMessages.Add(new InboxMessage
        {
            MessageId = messageId,
            Consumer = ConsumerName,
            ReceivedUtc = clock.UtcNow
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task CompleteInboxAsync(Guid messageId, CancellationToken cancellationToken)
    {
        InboxMessage inbox = await dbContext.InboxMessages
            .SingleAsync(item => item.MessageId == messageId && item.Consumer == ConsumerName, cancellationToken);
        inbox.ProcessedUtc = clock.UtcNow;
    }

    private static ExternalArtifactDescriptor ToArtifactDescriptor(StoredDocument document) =>
        new(
            document.Id,
            document.DocumentType.ToString(),
            document.OriginalFileName,
            document.ContentType,
            document.SizeBytes,
            document.UploadedUtc);

    private static void ApplyCanonicalStatus(AssistanceRequest request, ExternalApplicationStatus status)
    {
        string code = status.StatusCode.Trim().Replace('-', '_').Replace(' ', '_').ToUpperInvariant();
        if (code is "MORE_INFORMATION_REQUIRED" or "MORE_INFORMATION" or "AWAITING_INFORMATION")
        {
            if (request.Status == RequestStatus.Submitted)
            {
                request.RequestMoreInformation(ExternalSystemActorId, status.ChangedUtc, status.Detail ?? status.StatusText);
            }
            return;
        }

        if (code == "APPROVED" && request.Status == RequestStatus.Submitted)
        {
            request.Approve(ExternalSystemActorId, status.ChangedUtc);
            return;
        }

        if (code is "REJECTED" or "DECLINED")
        {
            if (request.Status == RequestStatus.Submitted)
            {
                request.Reject(ExternalSystemActorId, status.ChangedUtc, status.Detail ?? status.StatusText);
            }
            return;
        }

        if (code == "PENDING_FULFILMENT" && request.Status == RequestStatus.Approved)
        {
            request.MarkPendingFulfilment(ExternalSystemActorId, status.ChangedUtc);
            return;
        }

        if (code == "FULFILLED" && request.Status == RequestStatus.PendingFulfilment)
        {
            request.MarkFulfilled(ExternalSystemActorId, status.ChangedUtc);
            return;
        }

        if (code == "CLOSED" && request.Status == RequestStatus.Fulfilled)
        {
            request.Close(ExternalSystemActorId, status.ChangedUtc);
        }
    }

    private void AddStatusOutbox(AssistanceRequest request, DateTimeOffset occurredUtc)
    {
        AddOutbox(new RequestStatusChangedMessage(Guid.NewGuid(), occurredUtc, request.Id, (int)request.Status));
    }

    private void AddOutbox<TMessage>(TMessage message) where TMessage : IMessage
    {
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = message.MessageId,
            MessageType = typeof(TMessage).FullName ?? typeof(TMessage).Name,
            Payload = JsonSerializer.Serialize(message),
            OccurredUtc = message.OccurredUtc,
            AttemptCount = 0
        });
    }

    private void EnsureEnabled()
    {
        if (!options.Enabled)
        {
            throw new IntegrationTransferException(TransferFailures.For("Configuration", "CONFIGURATION"));
        }
    }
}
