using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Common.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Portal;
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
    private readonly IDiagnosticOperationsRouter diagnosticRouter;

    public PortalWorkflowService(
        RequestPortalDbContext dbContext,
        IClock clock,
        IDiagnosticOperationsRouter diagnosticRouter)
    {
        this.dbContext = dbContext;
        this.clock = clock;
        this.diagnosticRouter = diagnosticRouter;
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
                && organization.OrganizationType != "IndividualContext"
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

    public async Task<IReadOnlyList<OrganizationSummary>> GetRequestContextsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndividualContextAsync(userId, cancellationToken);

        List<OrganizationSummary> contexts = await (
            from membership in dbContext.OrganizationMemberships.AsNoTracking()
            join organization in dbContext.Organizations.AsNoTracking()
                on membership.OrganizationId equals organization.Id
            where membership.UserId == userId
                && membership.Status == MembershipStatus.Approved
                && organization.Status == OrganizationStatus.Approved
            orderby organization.OrganizationType == "IndividualContext" descending, organization.Name
            select new OrganizationSummary(
                organization.Id,
                organization.OrganizationType == "IndividualContext" ? "Myself" : organization.Name,
                organization.CountryCode,
                organization.Status.ToString(),
                membership.Status.ToString(),
                membership.Role.ToString(),
                organization.OrganizationType == "IndividualContext"))
            .ToListAsync(cancellationToken);

        return contexts;
    }

    public Task<IReadOnlyList<OrganizationSummary>> GetAvailableOrganizationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<OrganizationSummary> none = Array.Empty<OrganizationSummary>();
        return Task.FromResult(none);
    }

    private async Task EnsureIndividualContextAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        bool exists = await (
            from existingMembership in dbContext.OrganizationMemberships.AsNoTracking()
            join existingOrganization in dbContext.Organizations.AsNoTracking()
                on existingMembership.OrganizationId equals existingOrganization.Id
            where existingMembership.UserId == userId
                && existingOrganization.OrganizationType == "IndividualContext"
                && existingMembership.Status == MembershipStatus.Approved
            select existingMembership.Id)
            .AnyAsync(cancellationToken);

        if (exists)
        {
            return;
        }

        ApplicationUser user = await dbContext.Users
            .SingleAsync(item => item.Id == userId, cancellationToken);

        DateTimeOffset now = clock.UtcNow;
        string displayName = string.IsNullOrWhiteSpace(user.DisplayName)
            ? user.Email ?? user.UserName ?? "Individual Applicant"
            : user.DisplayName.Trim();

        Organization organization = new(
            Guid.NewGuid(),
            displayName,
            "JM",
            userId,
            now);

        organization.UpdateProfile(
            null,
            "IndividualContext",
            null,
            user.PhoneNumber,
            user.Email,
            displayName,
            null);

        OrganizationMembership membership = organization.RequestMembership(
            Guid.NewGuid(),
            userId,
            now);

        dbContext.Organizations.Add(organization);
        await dbContext.SaveChangesAsync(cancellationToken);

        organization.Approve(userId, now);
        await dbContext.SaveChangesAsync(cancellationToken);
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
            "RP-ORG-REGISTERED",
            DiagnosticSeverity.Information,
            "Organization",
            "Organization registration created.",
            organization.Id.ToString(),
            cancellationToken);

        return organization.Id;
    }

    public Task RequestMembershipAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException(
            "Organization membership is invitation-only. Ask an organization administrator to send an invitation to your account email address.");
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
            "RP-ORG-APPROVED",
            DiagnosticSeverity.Information,
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
        AddOutboxMessage(new MembershipApprovedMessage(
            Guid.NewGuid(),
            clock.UtcNow,
            organizationId,
            membership.Id,
            membership.UserId));

        await dbContext.SaveChangesAsync(cancellationToken);

        await WriteDiagnosticAsync(
            "RP-MEMBERSHIP-APPROVED",
            DiagnosticSeverity.Information,
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
        // PostgreSQL nextval is atomic and never rolls back a reserved value.
        // The sequence is global: years and deleted drafts cannot recycle codes.
        long number = await dbContext.Database
            .SqlQueryRaw<long>("SELECT nextval('request_code_sequence') AS \"Value\"")
            .SingleAsync(cancellationToken);
        string publicReference = $"RP-{now:yy}-{number:D5}";
        AssistanceRequest request = new(Guid.NewGuid(), publicReference, organizationId, userId, now);
        request.SetSummary(summary);

        dbContext.AssistanceRequests.Add(request);
        await dbContext.SaveChangesAsync(cancellationToken);

        await WriteDiagnosticAsync(
            "RP-REQUEST-CREATED",
            DiagnosticSeverity.Information,
            "AssistanceRequest",
            "Draft assistance request created.",
            request.PublicReference,
            cancellationToken);

        return request.Id;
    }

    public Task AddRequestItemAsync(
        Guid userId,
        Guid requestId,
        string description,
        decimal quantity,
        Guid unitOfMeasureId,
        CancellationToken cancellationToken = default) =>
        AddRequestItemAsync(userId, requestId, description, description, quantity, unitOfMeasureId, null, cancellationToken);

    public async Task AddRequestItemAsync(
        Guid userId,
        Guid requestId,
        string itemName,
        string description,
        decimal quantity,
        Guid unitOfMeasureId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.Items)
            .SingleAsync(item => item.Id == requestId, cancellationToken);

        await EnsureApprovedMemberAsync(userId, request.OrganizationId, cancellationToken);

        AssistanceRequestItem requestItem = new(
            Guid.NewGuid(),
            request.Id,
            itemName,
            description,
            quantity,
            unitOfMeasureId,
            notes);

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
            "RP-REQUEST-SUBMITTED",
            DiagnosticSeverity.Information,
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
            .Select(item => new RequestItemDetail(item.Id, item.ItemName, item.Description, item.Quantity, item.UnitOfMeasureId, item.Notes))
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
        await WriteRequestDecisionDiagnosticAsync(request, "RP-REQUEST-APPROVED", "Assistance request approved.", cancellationToken);
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
        await WriteRequestDecisionDiagnosticAsync(request, "RP-REQUEST-REJECTED", "Assistance request rejected.", cancellationToken);
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
        await WriteRequestDecisionDiagnosticAsync(request, "RP-REQUEST-MORE-INFO", "Additional information requested.", cancellationToken);
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
                "RP-AUTH-ORG-DENIED",
                DiagnosticSeverity.Warning,
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
            DiagnosticSeverity.Information,
            "AssistanceRequest",
            message,
            request.PublicReference,
            cancellationToken);
    }

    private async Task WriteDiagnosticAsync(
        string code,
        DiagnosticSeverity severity,
        string component,
        string message,
        string? detail,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string>? properties = string.IsNullOrWhiteSpace(detail)
            ? null
            : new Dictionary<string, string>
            {
                ["Detail"] = detail
            };

        DiagnosticTelemetryEvent diagnosticEvent = new(
            clock.UtcNow,
            $"RequestPortal.{component}",
            code,
            severity,
            message,
            Properties: properties);

        try
        {
            await diagnosticRouter.RouteAsync(
                diagnosticEvent,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Diagnostics must never block or roll back a successful business operation.
        }
    }
}
