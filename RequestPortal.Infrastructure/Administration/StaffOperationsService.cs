using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Auditing;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Notifications;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Administration;

public sealed class StaffOperationsService : IStaffOperationsService
{
    private const int MaxOutboxAttempts = 10;
    private readonly RequestPortalDbContext dbContext;
    private readonly IClock clock;
    private readonly IBusinessAuditWriter auditWriter;
    private readonly IConfiguration configuration;

    public StaffOperationsService(
        RequestPortalDbContext dbContext,
        IClock clock,
        IBusinessAuditWriter auditWriter,
        IConfiguration configuration)
    {
        this.dbContext = dbContext;
        this.clock = clock;
        this.auditWriter = auditWriter;
        this.configuration = configuration;
    }

    public async Task<StaffOperationsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.UtcNow;
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        int[] configuredWarningDays = configuration.GetSection("Organizations:RenewalWarningDays").Get<int[]>() ?? [60, 30, 7];
        int reverificationWarningDays = configuredWarningDays.Where(day => day > 0).DefaultIfEmpty(60).Max();
        DateOnly warningThrough = today.AddDays(reverificationWarningDays);

        List<PendingOrganizationRow> pendingOrganizationRows = await (from organization in dbContext.Organizations.AsNoTracking() where organization.Status == OrganizationStatus.PendingApproval orderby organization.CreatedUtc select new PendingOrganizationRow(organization.Id, organization.Name, organization.CountryCode, organization.CreatedUtc)).Take(100).ToListAsync(cancellationToken);
        List<ReverificationOrganizationRow> reverificationRows = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization =>
                organization.RegistrationExpiryDate != null &&
                organization.RegistrationExpiryDate <= warningThrough &&
                (organization.Status == OrganizationStatus.Approved || organization.Status == OrganizationStatus.Suspended))
            .OrderBy(organization => organization.RegistrationExpiryDate)
            .Select(organization => new ReverificationOrganizationRow(
                organization.Id,
                organization.Name,
                organization.CountryCode,
                organization.RegistrationExpiryDate!.Value,
                organization.Status.ToString()))
            .Take(200)
            .ToListAsync(cancellationToken);
        List<RequestQueueRow> reviewRows = await (from request in dbContext.AssistanceRequests.AsNoTracking() join organization in dbContext.Organizations.AsNoTracking() on request.OrganizationId equals organization.Id where request.Status == RequestStatus.Submitted orderby request.SubmittedUtc ?? request.CreatedUtc select new RequestQueueRow(request.Id, request.PublicReference, organization.Id, organization.Name, request.Status, request.SubmittedUtc, request.CreatedUtc)).Take(200).ToListAsync(cancellationToken);
        List<RequestQueueRow> actionRows = await (from request in dbContext.AssistanceRequests.AsNoTracking() join organization in dbContext.Organizations.AsNoTracking() on request.OrganizationId equals organization.Id where request.Status == RequestStatus.MoreInformationRequired orderby request.SubmittedUtc ?? request.CreatedUtc select new RequestQueueRow(request.Id, request.PublicReference, organization.Id, organization.Name, request.Status, request.SubmittedUtc, request.CreatedUtc)).Take(200).ToListAsync(cancellationToken);
        var documentRows = await (
            from document in dbContext.Documents.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking() on document.OrganizationId equals organization.Id into organizations
            from organization in organizations.DefaultIfEmpty()
            join request in dbContext.AssistanceRequests.AsNoTracking() on document.OwnerId equals request.Id into requests
            from request in requests.DefaultIfEmpty()
            join intake in dbContext.QuickIntakes.AsNoTracking() on document.OwnerId equals intake.RequestId into intakes
            from intake in intakes.DefaultIfEmpty()
            join user in dbContext.Users.AsNoTracking() on (request.SubmittedByUserId ?? request.CreatedByUserId) equals user.Id into users
            from user in users.DefaultIfEmpty()
            where document.VerificationStatus == DocumentVerificationStatus.Pending && !document.IsArchived
            orderby document.UploadedUtc, document.Id
            select new
            {
                Document = document,
                OrganizationName = organization == null ? "Organization unavailable" : organization.Name,
                RequestId = document.OwnerType == DocumentOwnerType.AssistanceRequest ? (Guid?)document.OwnerId : null,
                ApplicantName = intake != null ? (intake.FirstName + " " + intake.LastName).Trim()
                    : user != null && ((user.FirstName ?? "") + " " + (user.LastName ?? "")).Trim() != ""
                        ? ((user.FirstName ?? "") + " " + (user.LastName ?? "")).Trim()
                        : user != null && user.DisplayName != "" ? user.DisplayName : "Name not provided",
                HasValidContext = organization != null &&
                    ((document.OwnerType == DocumentOwnerType.Organization && document.OwnerId == document.OrganizationId) ||
                    (document.OwnerType == DocumentOwnerType.AssistanceRequest && request != null && request.OrganizationId == document.OrganizationId))
            }).Take(200).ToListAsync(cancellationToken);
        List<DeadLetterWorkItem> deadLetters = await dbContext.OutboxMessages.AsNoTracking().Where(message => message.ProcessedUtc == null && (message.IsTerminal || message.AttemptCount >= MaxOutboxAttempts)).OrderBy(message => message.OccurredUtc).Select(message => new DeadLetterWorkItem(message.Id, message.MessageType, message.OccurredUtc, message.AttemptCount, message.LastError, message.RequestId)).Take(200).ToListAsync(cancellationToken);
        List<NotificationDeliveryWorkItem> failedDeliveries = await dbContext.NotificationHistory
            .AsNoTracking()
            .Where(item => item.Status == NotificationDeliveryStatus.Failed || item.Status == NotificationDeliveryStatus.DeadLettered)
            .OrderByDescending(item => item.CreatedUtc)
            .Select(item => new NotificationDeliveryWorkItem(
                item.Id,
                item.Channel,
                item.Destination,
                item.Subject,
                item.Status,
                item.CreatedUtc,
                item.LastError))
            .Take(200)
            .ToListAsync(cancellationToken);

        return new StaffOperationsSnapshot(
            pendingOrganizationRows.Select(item => new PendingOrganizationWorkItem(item.OrganizationId, item.OrganizationName, item.CountryCode, item.CreatedUtc, CalculateAgeDays(item.CreatedUtc, now))).ToArray(),
            reverificationRows.Select(item => new OrganizationReverificationWorkItem(item.OrganizationId, item.OrganizationName, item.CountryCode, item.ExpiryDate, item.Status, item.ExpiryDate.DayNumber - today.DayNumber)).ToArray(),
            reviewRows.Select(item => ToRequestWorkItem(item, now)).ToArray(),
            actionRows.Select(item => ToRequestWorkItem(item, now)).ToArray(),
            documentRows.Select(item => new DocumentWorkItem(item.Document.Id, item.Document.OrganizationId, item.OrganizationName, item.Document.DocumentType, item.Document.OriginalFileName, item.Document.UploadedUtc, CalculateAgeDays(item.Document.UploadedUtc, now), item.RequestId, item.RequestId.HasValue ? item.ApplicantName : null, item.HasValidContext)).ToArray(),
            deadLetters,
            failedDeliveries);
    }

    public async Task ReplayDeadLetterAsync(Guid staffUserId, Guid messageId, CancellationToken cancellationToken = default)
    {
        OutboxMessage message = await dbContext.OutboxMessages.SingleAsync(item => item.Id == messageId, cancellationToken);
        if(message.MessageType.EndsWith("FfpManagerSubmissionRequestedMessage",StringComparison.Ordinal))
            throw new InvalidOperationException("Use integration diagnostics to reconcile and safely retry this transfer.");
        if (message.ProcessedUtc.HasValue)
        {
            throw new InvalidOperationException("Processed messages cannot be replayed.");
        }
        if (!message.IsTerminal && message.AttemptCount < MaxOutboxAttempts)
        {
            throw new InvalidOperationException("Only terminal dead-letter messages can be manually replayed.");
        }

        message.AttemptCount = 0;
        message.LastError = null;
        message.NextAttemptUtc = null;
        message.IsTerminal = false;
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(staffUserId, "OutboxDeadLetterReplayed", "OutboxMessage", message.Id, null, message.MessageType, cancellationToken);
    }

    private static RequestWorkItem ToRequestWorkItem(RequestQueueRow item, DateTimeOffset now)
    {
        DateTimeOffset ageFrom = item.SubmittedUtc ?? item.CreatedUtc;
        return new RequestWorkItem(item.RequestId, item.PublicReference, item.OrganizationId, item.OrganizationName, item.Status, item.SubmittedUtc, item.CreatedUtc, CalculateAgeDays(ageFrom, now));
    }

    private static int CalculateAgeDays(DateTimeOffset fromUtc, DateTimeOffset nowUtc) => (int)Math.Floor(Math.Max(0, (nowUtc - fromUtc).TotalDays));

    private sealed record PendingOrganizationRow(Guid OrganizationId, string OrganizationName, string CountryCode, DateTimeOffset CreatedUtc);
    private sealed record ReverificationOrganizationRow(Guid OrganizationId, string OrganizationName, string CountryCode, DateOnly ExpiryDate, string Status);
    private sealed record RequestQueueRow(Guid RequestId, string PublicReference, Guid OrganizationId, string OrganizationName, RequestStatus Status, DateTimeOffset? SubmittedUtc, DateTimeOffset CreatedUtc);
}
