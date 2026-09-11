using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Portal;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Portal;

public sealed class PortalWorkflowService : IPortalWorkflowService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IClock clock;
    private readonly IStarfleetDiagnosticSink diagnosticSink;

    public PortalWorkflowService(
        RequestPortalDbContext dbContext,
        IClock clock,
        IStarfleetDiagnosticSink diagnosticSink)
    {
        this.dbContext = dbContext;
        this.clock = clock;
        this.diagnosticSink = diagnosticSink;
    }

    public async Task<IReadOnlyList<OrganizationSummary>> GetOrganizationsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        List<OrganizationSummary> organizations = await (
            from membership in dbContext.OrganizationMemberships.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on membership.OrganizationId equals organization.Id
            where membership.UserId == userId
            orderby organization.Name
            select new OrganizationSummary(
                organization.Id,
                organization.Name,
                organization.CountryCode,
                organization.Status.ToString(),
                membership.Status.ToString(),
                membership.Role.ToString()))
            .ToListAsync(cancellationToken);

        return organizations;
    }

    public async Task<IReadOnlyList<OrganizationSummary>> GetAvailableOrganizationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        List<Guid> existingOrganizationIds = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == userId && membership.Status != MembershipStatus.Revoked)
            .Select(membership => membership.OrganizationId)
            .ToListAsync(cancellationToken);

        List<OrganizationSummary> organizations = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization =>
                organization.Status == OrganizationStatus.Approved &&
                !existingOrganizationIds.Contains(organization.Id))
            .OrderBy(organization => organization.Name)
            .Select(organization => new OrganizationSummary(
                organization.Id,
                organization.Name,
                organization.CountryCode,
                organization.Status.ToString(),
                string.Empty,
                string.Empty))
            .ToListAsync(cancellationToken);

        return organizations;
    }

    public async Task<Guid> RegisterOrganizationAsync(
        Guid userId,
        string name,
        string countryCode,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = clock.UtcNow;
        Organization organization = new(Guid.NewGuid(), name, countryCode, userId, now);
        organization.RequestMembership(Guid.NewGuid(), userId, now);

        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync(cancellationToken);

        await WriteDiagnosticAsync(
            "SF-ORG-REGISTERED",
            StarfleetSeverity.Information,
            "Organization",
            "Organization registration created.",
            organization.Id.ToString(),
            cancellationToken);

        return organization.Id;
    }

    public async Task RequestMembershipAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        Organization organization = await dbContext.Organizations
            .Include(item => item.Memberships)
            .SingleAsync(item => item.Id == organizationId, cancellationToken);

        if (organization.Status != OrganizationStatus.Approved)
        {
            throw new InvalidOperationException("Membership can only be requested for an approved organization.");
        }

        organization.RequestMembership(Guid.NewGuid(), userId, clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        await WriteDiagnosticAsync(
            "SF-MEMBERSHIP-REQUESTED",
            StarfleetSeverity.Information,
            "OrganizationMembership",
            "Organization membership requested.",
            organizationId.ToString(),
            cancellationToken);
    }

    public async Task<IReadOnlyList<OrganizationSummary>> GetPendingOrganizationsAsync(
        CancellationToken cancellationToken = default)
    {
        List<OrganizationSummary> organizations = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Status == OrganizationStatus.PendingApproval)
            .OrderBy(organization => organization.CreatedUtc)
            .Select(organization => new OrganizationSummary(
                organization.Id,
                organization.Name,
                organization.CountryCode,
                organization.Status.ToString(),
                string.Empty,
                string.Empty))
            .ToListAsync(cancellationToken);

        return organizations;
    }

    public async Task ApproveOrganizationAsync(
        Guid staffUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        Organization organization = await dbContext.Organizations
            .Include(item => item.Memberships)
            .SingleAsync(item => item.Id == organizationId, cancellationToken);

        DateTimeOffset now = clock.UtcNow;
        organization.Approve(staffUserId, now);
        AddOutboxMessage(new OrganizationApprovedMessage(Guid.NewGuid(), now, organizationId));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await WriteDiagnosticAsync(
            "SF-ORG-APPROVED",
            StarfleetSeverity.Information,
            "Organization",
            "Organization approved by FFP staff.",
            organizationId.ToString(),
            cancellationToken);
    }

    public async Task<IReadOnlyList<PendingMembershipSummary>> GetPendingMembershipsAsync(
        Guid administratorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdministratorAsync(administratorUserId, organizationId, cancellationToken);

        List<PendingMembershipSummary> memberships = await (
            from membership in dbContext.OrganizationMemberships.AsNoTracking()
            join user in dbContext.Users.AsNoTracking()
                on membership.UserId equals user.Id
            where membership.OrganizationId == organizationId && membership.Status == MembershipStatus.Pending
            orderby membership.RequestedUtc
            select new PendingMembershipSummary(
                membership.Id,
                organizationId,
                user.Id,
                user.DisplayName,
                user.Email ?? string.Empty,
                membership.RequestedUtc))
            .ToListAsync(cancellationToken);

        return memberships;
    }

    public async Task ApproveMembershipAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdministratorAsync(administratorUserId, organizationId, cancellationToken);

        OrganizationMembership membership = await dbContext.OrganizationMemberships
            .SingleAsync(
                item => item.Id == membershipId && item.OrganizationId == organizationId,
                cancellationToken);

        membership.Approve(administratorUserId, clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        await WriteDiagnosticAsync(
            "SF-MEMBERSHIP-APPROVED",
            StarfleetSeverity.Information,
            "OrganizationMembership",
            "Organization representative approved.",
            membershipId.ToString(),
            cancellationToken);
    }

    public async Task<Guid> CreateRequestAsync(
        Guid userId,
        Guid organizationId,
        string summary,
        CancellationToken cancellationToken = default)
    {
        await EnsureApprovedMemberAsync(userId, organizationId, cancellationToken);

        DateTimeOffset now = clock.UtcNow;
        string publicReference = $"RP-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        AssistanceRequest request = new(Guid.NewGuid(), publicReference, organizationId, userId, now);
        request.SetSummary(summary);

        dbContext.AssistanceRequests.Add(request);
        await dbContext.SaveChangesAsync(cancellationToken);

        await WriteDiagnosticAsync(
            "SF-REQUEST-CREATED",
            StarfleetSeverity.Information,
            "AssistanceRequest",
            "Draft assistance request created.",
            request.PublicReference,
            cancellationToken);

        return request.Id;
    }

    public async Task AddRequestItemAsync(
        Guid userId,
        Guid requestId,
        string description,
        decimal quantity,
        Guid unitOfMeasureId,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.Items)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        await EnsureApprovedMemberAsync(userId, request.OrganizationId, cancellationToken);

        AssistanceRequestItem requestItem = new(
            Guid.NewGuid(),
            request.Id,
            description,
            quantity,
            unitOfMeasureId);

        request.AddItem(requestItem);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SubmitRequestAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.Items)
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        await EnsureApprovedMemberAsync(userId, request.OrganizationId, cancellationToken);

        DateTimeOffset now = clock.UtcNow;
        request.Submit(userId, now);
        AddOutboxMessage(new RequestSubmittedMessage(Guid.NewGuid(), now, request.Id, request.OrganizationId));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await WriteDiagnosticAsync(
            "SF-REQUEST-SUBMITTED",
            StarfleetSeverity.Information,
            "AssistanceRequest",
            "Assistance request submitted for FFP review.",
            request.PublicReference,
            cancellationToken);
    }

    public async Task ResubmitRequestAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        await EnsureApprovedMemberAsync(userId, request.OrganizationId, cancellationToken);
        request.Resubmit(userId, clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RequestSummary>> GetRequestsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Guid> authorizedOrganizationIds = dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(membership => membership.UserId == userId && membership.Status == MembershipStatus.Approved)
            .Select(membership => membership.OrganizationId);

        List<RequestSummary> requests = await (
            from request in dbContext.AssistanceRequests.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on request.OrganizationId equals organization.Id
            where authorizedOrganizationIds.Contains(request.OrganizationId)
            orderby request.CreatedUtc descending
            select new RequestSummary(
                request.Id,
                request.PublicReference,
                request.OrganizationId,
                organization.Name,
                request.Status.ToString(),
                request.Summary,
                request.CreatedUtc,
                request.SubmittedUtc))
            .ToListAsync(cancellationToken);

        return requests;
    }

    public async Task<IReadOnlyList<RequestSummary>> GetSubmittedRequestsAsync(
        CancellationToken cancellationToken = default)
    {
        List<RequestSummary> requests = await (
            from request in dbContext.AssistanceRequests.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on request.OrganizationId equals organization.Id
            where request.Status == RequestStatus.Submitted
            orderby request.SubmittedUtc
            select new RequestSummary(
                request.Id,
                request.PublicReference,
                request.OrganizationId,
                organization.Name,
                request.Status.ToString(),
                request.Summary,
                request.CreatedUtc,
                request.SubmittedUtc))
            .ToListAsync(cancellationToken);

        return requests;
    }

    public async Task<RequestDetail?> GetRequestAsync(
        Guid userId,
        Guid requestId,
        bool isStaff,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest? request = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Include(item => item.Items)
            .Include(item => item.StatusHistory)
            .SingleOrDefaultAsync(item => item.Id == requestId, cancellationToken);

        if (request is null)
        {
            return null;
        }

        if (!isStaff)
        {
            await EnsureApprovedMemberAsync(userId, request.OrganizationId, cancellationToken);
        }

        string organizationName = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.Id == request.OrganizationId)
            .Select(organization => organization.Name)
            .SingleAsync(cancellationToken);

        List<RequestItemDetail> items = request.Items
            .Select(item => new RequestItemDetail(item.Id, item.Description, item.Quantity, item.UnitOfMeasureId))
            .ToList();

        List<RequestHistoryDetail> history = request.StatusHistory
            .OrderByDescending(item => item.ChangedUtc)
            .Select(item => new RequestHistoryDetail(item.Status.ToString(), item.ChangedUtc, item.Reason))
            .ToList();

        return new RequestDetail(
            request.Id,
            request.PublicReference,
            request.OrganizationId,
            organizationName,
            request.Status.ToString(),
            request.Summary,
            items,
            history);
    }

    public async Task ApproveRequestAsync(
        Guid staffUserId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await LoadSubmittedRequestAsync(requestId, cancellationToken);
        DateTimeOffset now = clock.UtcNow;
        request.Approve(staffUserId, now);
        AddOutboxMessage(new RequestStatusChangedMessage(Guid.NewGuid(), now, request.Id, (int)request.Status));
        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteRequestDecisionDiagnosticAsync(request, "SF-REQUEST-APPROVED", "Assistance request approved.", cancellationToken);
    }

    public async Task RejectRequestAsync(
        Guid staffUserId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await LoadSubmittedRequestAsync(requestId, cancellationToken);
        DateTimeOffset now = clock.UtcNow;
        request.Reject(staffUserId, now, reason);
        AddOutboxMessage(new RequestStatusChangedMessage(Guid.NewGuid(), now, request.Id, (int)request.Status));
        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteRequestDecisionDiagnosticAsync(request, "SF-REQUEST-REJECTED", "Assistance request rejected.", cancellationToken);
    }

    public async Task RequestMoreInformationAsync(
        Guid staffUserId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await LoadSubmittedRequestAsync(requestId, cancellationToken);
        DateTimeOffset now = clock.UtcNow;
        request.RequestMoreInformation(staffUserId, now, reason);
        AddOutboxMessage(new RequestStatusChangedMessage(Guid.NewGuid(), now, request.Id, (int)request.Status));
        await dbContext.SaveChangesAsync(cancellationToken);
        await WriteRequestDecisionDiagnosticAsync(request, "SF-REQUEST-MORE-INFO", "Additional information requested.", cancellationToken);
    }

    private async Task<AssistanceRequest> LoadSubmittedRequestAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        return await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId, cancellationToken);
    }

    private async Task EnsureApprovedMemberAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        bool authorized = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(
                membership =>
                    membership.UserId == userId &&
                    membership.OrganizationId == organizationId &&
                    membership.Status == MembershipStatus.Approved,
                cancellationToken);

        if (!authorized)
        {
            await WriteDiagnosticAsync(
                "SF-AUTH-ORG-DENIED",
                StarfleetSeverity.Warning,
                "Security.Authorization",
                "Organization access denied.",
                organizationId.ToString(),
                cancellationToken);

            throw new UnauthorizedAccessException("The current user is not an approved representative of this organization.");
        }
    }

    private async Task EnsureAdministratorAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        bool authorized = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(
                membership =>
                    membership.UserId == userId &&
                    membership.OrganizationId == organizationId &&
                    membership.Status == MembershipStatus.Approved &&
                    membership.Role == OrganizationRole.Administrator,
                cancellationToken);

        if (!authorized)
        {
            throw new UnauthorizedAccessException("Organization administrator access is required.");
        }
    }

    private void AddOutboxMessage<TMessage>(TMessage message)
        where TMessage : IMessage
    {
        OutboxMessage outboxMessage = new()
        {
            Id = message.MessageId,
            MessageType = typeof(TMessage).FullName ?? typeof(TMessage).Name,
            Payload = JsonSerializer.Serialize(message),
            OccurredUtc = message.OccurredUtc,
            AttemptCount = 0
        };

        dbContext.OutboxMessages.Add(outboxMessage);
    }

    private async Task WriteRequestDecisionDiagnosticAsync(
        AssistanceRequest request,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        await WriteDiagnosticAsync(
            code,
            StarfleetSeverity.Information,
            "AssistanceRequest",
            message,
            request.PublicReference,
            cancellationToken);
    }

    private Task WriteDiagnosticAsync(
        string code,
        StarfleetSeverity severity,
        string component,
        string message,
        string? detail,
        CancellationToken cancellationToken)
    {
        StarfleetDiagnosticEvent diagnosticEvent = new(
            Guid.NewGuid(),
            clock.UtcNow,
            component,
            code,
            severity,
            message,
            detail);

        return diagnosticSink.WriteAsync(diagnosticEvent, cancellationToken);
    }
}
