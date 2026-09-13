using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Administration;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Documents;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Administration;

public sealed class StaffOperationsService : IStaffOperationsService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IClock clock;

    public StaffOperationsService(RequestPortalDbContext dbContext, IClock clock)
    {
        this.dbContext = dbContext;
        this.clock = clock;
    }

    public async Task<StaffOperationsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.UtcNow;

        List<PendingOrganizationRow> pendingOrganizationRows = await (
            from organization in dbContext.Organizations.AsNoTracking()
            where organization.Status == OrganizationStatus.PendingApproval
            orderby organization.CreatedUtc
            select new PendingOrganizationRow(
                organization.Id,
                organization.Name,
                organization.CountryCode,
                organization.CreatedUtc))
            .Take(100)
            .ToListAsync(cancellationToken);

        List<RequestQueueRow> reviewRows = await (
            from request in dbContext.AssistanceRequests.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on request.OrganizationId equals organization.Id
            where request.Status == RequestStatus.Submitted
            orderby request.SubmittedUtc ?? request.CreatedUtc
            select new RequestQueueRow(
                request.Id,
                request.PublicReference,
                organization.Id,
                organization.Name,
                request.Status,
                request.SubmittedUtc,
                request.CreatedUtc))
            .Take(200)
            .ToListAsync(cancellationToken);

        List<RequestQueueRow> actionRows = await (
            from request in dbContext.AssistanceRequests.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on request.OrganizationId equals organization.Id
            where request.Status == RequestStatus.MoreInformationRequired
            orderby request.SubmittedUtc ?? request.CreatedUtc
            select new RequestQueueRow(
                request.Id,
                request.PublicReference,
                organization.Id,
                organization.Name,
                request.Status,
                request.SubmittedUtc,
                request.CreatedUtc))
            .Take(200)
            .ToListAsync(cancellationToken);

        List<DocumentQueueRow> documentRows = await (
            from document in dbContext.Documents.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on document.OrganizationId equals organization.Id
            where document.VerificationStatus == DocumentVerificationStatus.Pending && !document.IsArchived
            orderby document.UploadedUtc
            select new DocumentQueueRow(
                document.Id,
                organization.Id,
                organization.Name,
                document.DocumentType,
                document.OriginalFileName,
                document.UploadedUtc))
            .Take(200)
            .ToListAsync(cancellationToken);

        PendingOrganizationWorkItem[] pendingOrganizations = pendingOrganizationRows
            .Select(item => new PendingOrganizationWorkItem(
                item.OrganizationId,
                item.OrganizationName,
                item.CountryCode,
                item.CreatedUtc,
                CalculateAgeDays(item.CreatedUtc, now)))
            .ToArray();

        RequestWorkItem[] openReviewRequests = reviewRows
            .Select(item => ToRequestWorkItem(item, now))
            .ToArray();

        RequestWorkItem[] actionRequiredRequests = actionRows
            .Select(item => ToRequestWorkItem(item, now))
            .ToArray();

        DocumentWorkItem[] documentsAwaitingVerification = documentRows
            .Select(item => new DocumentWorkItem(
                item.DocumentId,
                item.OrganizationId,
                item.OrganizationName,
                item.DocumentType,
                item.OriginalFileName,
                item.UploadedUtc,
                CalculateAgeDays(item.UploadedUtc, now)))
            .ToArray();

        return new StaffOperationsSnapshot(
            pendingOrganizations,
            openReviewRequests,
            actionRequiredRequests,
            documentsAwaitingVerification);
    }

    private static RequestWorkItem ToRequestWorkItem(RequestQueueRow item, DateTimeOffset now)
    {
        DateTimeOffset ageFrom = item.SubmittedUtc ?? item.CreatedUtc;

        return new RequestWorkItem(
            item.RequestId,
            item.PublicReference,
            item.OrganizationId,
            item.OrganizationName,
            item.Status,
            item.SubmittedUtc,
            item.CreatedUtc,
            CalculateAgeDays(ageFrom, now));
    }

    private static int CalculateAgeDays(DateTimeOffset fromUtc, DateTimeOffset nowUtc)
    {
        double days = Math.Max(0, (nowUtc - fromUtc).TotalDays);
        return (int)Math.Floor(days);
    }

    private sealed record PendingOrganizationRow(
        Guid OrganizationId,
        string OrganizationName,
        string CountryCode,
        DateTimeOffset CreatedUtc);

    private sealed record RequestQueueRow(
        Guid RequestId,
        string PublicReference,
        Guid OrganizationId,
        string OrganizationName,
        RequestStatus Status,
        DateTimeOffset? SubmittedUtc,
        DateTimeOffset CreatedUtc);

    private sealed record DocumentQueueRow(
        Guid DocumentId,
        Guid OrganizationId,
        string OrganizationName,
        DocumentType DocumentType,
        string OriginalFileName,
        DateTimeOffset UploadedUtc);
}
