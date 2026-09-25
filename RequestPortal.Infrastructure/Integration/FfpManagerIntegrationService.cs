using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

    public FfpManagerIntegrationService(
        RequestPortalDbContext dbContext,
        IDocumentService documentService,
        IExternalApplicationBridgeClient bridgeClient,
        IOptions<FfpManagerBridgeOptions> options,
        IClock clock,
        IPortalMessageService portalMessageService)
    {
        this.dbContext = dbContext;
        this.documentService = documentService;
        this.bridgeClient = bridgeClient;
        this.options = options.Value;
        this.clock = clock;
        this.portalMessageService = portalMessageService;
    }

    public async Task<ExternalApplicationReference> SubmitRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken = default)
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
                item.Email
            })
            .SingleAsync(cancellationToken);

        IntegrationLink link = await GetOrCreateLinkAsync(request.Id, cancellationToken);
        link.MarkPending(clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
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

            ExternalApplicationSubmission submission = new(
                link.Id,
                request.Id,
                request.PublicReference,
                organization.Id,
                organization.Name,
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
                artifacts);

            ExternalApplicationReference reference = await bridgeClient.SubmitApplicationAsync(submission, cancellationToken);

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

            link.MarkSynchronized(reference.ApplicationId, clock.UtcNow);
            await dbContext.SaveChangesAsync(cancellationToken);
            return reference;
        }
        catch (Exception exception)
        {
            link.MarkFailed(clock.UtcNow, exception.Message);
            await dbContext.SaveChangesAsync(cancellationToken);
            throw;
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
                thread.StaffVisible)
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
            cancellationToken);

        ExternalInformationRequestedMessage message = new(
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
                thread.StaffVisible)
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
            attachmentDocumentIds: documentIds);

        if (request.Status == RequestStatus.MoreInformationRequired)
        {
            DateTimeOffset now = clock.UtcNow;
            request.Resubmit(userId, now);
            AddStatusOutbox(request, now);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
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
            throw new InvalidOperationException("FFP Manager integration is disabled.");
        }
    }
}
