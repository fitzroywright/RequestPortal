using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Administration;
using RequestPortal.Application.Auditing;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Administration;

public sealed class StaffOperationsService : IStaffOperationsService
{
    private const int MaxOutboxAttempts = 10;
    private const int ReverificationWarningDays = 60;
    private readonly RequestPortalDbContext dbContext;
    private readonly IClock clock;
    private readonly IBusinessAuditWriter auditWriter;

    public StaffOperationsService(RequestPortalDbContext dbContext, IClock clock, IBusinessAuditWriter auditWriter)
    {
        this.dbContext = dbContext;
        this.clock = clock;
        this.auditWriter = auditWriter;
    }

    public async Task<StaffOperationsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.UtcNow;
        DateOnly today = DateOnly.FromDateTime(now.UtcDateTime);
        DateOnly warningThrough = today.AddDays(ReverificationWarningDays);

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
        List<DocumentQueueRow> documentRows = await (from document in dbContext.Documents.AsNoTracking() join organization in dbContext.Organizations.AsNoTracking() on document.OrganizationId equals organization.Id where document.VerificationStatus == DocumentVerificationStatus.Pending && !document.IsArchived orderby document.UploadedUtc select new DocumentQueueRow(document.Id, organization.Id, organization.Name, document.DocumentType, document.OriginalFileName, document.UploadedUtc)).Take(200).ToListAsync(cancellationToken);
        List<DeadLetterWorkItem> deadLetters = await dbContext.OutboxMessages.AsNoTracking().Where(message => message.ProcessedUtc == null && message.AttemptCount >= MaxOutboxAttempts).OrderBy(message => message.OccurredUtc).Select(message => new DeadLetterWorkItem(message.Id, message.MessageType, message.OccurredUtc, message.AttemptCount, message.LastError)).Take(200).ToListAsync(cancellationToken);

        return new StaffOperationsSnapshot(
            pendingOrganizationRows.Select(item => new PendingOrganizationWorkItem(item.OrganizationId, item.OrganizationName, item.CountryCode, item.CreatedUtc, CalculateAgeDays(item.CreatedUtc, now))).ToArray(),
            reverificationRows.Select(item => new OrganizationReverificationWorkItem(item.OrganizationId, item.OrganizationName, item.CountryCode, item.ExpiryDate, item.Status, item.ExpiryDate.DayNumber - today.DayNumber)).ToArray(),
            reviewRows.Select(item => ToRequestWorkItem(item, now)).ToArray(),
            actionRows.Select(item => ToRequestWorkItem(item, now)).ToArray(),
            documentRows.Select(item => new DocumentWorkItem(item.DocumentId, item.OrganizationId, item.OrganizationName, item.DocumentType, item.OriginalFileName, item.UploadedUtc, CalculateAgeDays(item.UploadedUtc, now))).ToArray(),
            deadLetters);
    }

    public async Task ReplayDeadLetterAsync(Guid staffUserId, Guid messageId, CancellationToken cancellationToken = default)
    {
        OutboxMessage message = await dbContext.OutboxMessages.SingleAsync(item => item.Id == messageId, cancellationToken);
        if (message.ProcessedUtc.HasValue)
        {
            throw new InvalidOperationException("Processed messages cannot be replayed.");
        }
        if (message.AttemptCount < MaxOutboxAttempts)
        {
            throw new InvalidOperationException("Only terminal dead-letter messages can be manually replayed.");
        }

        message.AttemptCount = 0;
        message.LastError = null;
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
    private sealed record DocumentQueueRow(Guid DocumentId, Guid OrganizationId, string OrganizationName, DocumentType DocumentType, string OriginalFileName, DateTimeOffset UploadedUtc);
}
