using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Organizations;
using RequestPortal.Common.Messaging;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Domain.Requests;

namespace RequestPortal.Infrastructure.Organizations;

public sealed class OrganizationMembershipGovernanceService : IOrganizationMembershipGovernanceService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IClock clock;
    private readonly IBusinessAuditWriter auditWriter;

    public OrganizationMembershipGovernanceService(
        RequestPortalDbContext dbContext,
        IClock clock,
        IBusinessAuditWriter auditWriter)
    {
        this.dbContext = dbContext;
        this.clock = clock;
        this.auditWriter = auditWriter;
    }

    public async Task<IReadOnlyList<OrganizationMembershipView>> GetMembershipsAsync(
        Guid administratorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdministratorAsync(administratorUserId, organizationId, cancellationToken);

        List<OrganizationMembershipView> memberships = await (
            from membership in dbContext.OrganizationMemberships.AsNoTracking()
            join user in dbContext.Users.AsNoTracking() on membership.UserId equals user.Id
            where membership.OrganizationId == organizationId
            orderby membership.Status, membership.Role descending, user.DisplayName
            select new OrganizationMembershipView(
                membership.Id,
                membership.OrganizationId,
                membership.UserId,
                user.DisplayName,
                user.Email ?? string.Empty,
                membership.Status.ToString(),
                membership.Role.ToString(),
                membership.RequestedUtc,
                membership.ApprovedUtc,
                membership.EndedUtc,
                membership.EndReason))
            .ToListAsync(cancellationToken);

        return memberships;
    }

    public async Task ApproveAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken = default)
    {
        Organization organization = await LoadForAdministrationAsync(administratorUserId, organizationId, cancellationToken);
        OrganizationMembership membership = organization.Memberships.Single(item => item.Id == membershipId);
        membership.Approve(administratorUserId, clock.UtcNow);
        var approvedUser = await dbContext.Users.SingleAsync(item => item.Id == membership.UserId, cancellationToken);
        approvedUser.IndividualProfileUpdateRequired = false;
        approvedUser.IndividualProfileUpdateRequiredUtc = null;
        AddMembershipChangedOutbox(organizationId, membership, "Approved", null);
        await SaveAndAuditAsync(administratorUserId, organizationId, membershipId, "MembershipApproved", null, cancellationToken);
    }

    public async Task RejectAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        Organization organization = await LoadForAdministrationAsync(administratorUserId, organizationId, cancellationToken);
        organization.RejectMembership(membershipId, administratorUserId, clock.UtcNow, reason);
        OrganizationMembership membership = organization.Memberships.Single(item => item.Id == membershipId);
        AddMembershipChangedOutbox(organizationId, membership, "Rejected", reason);
        await SaveAndAuditAsync(administratorUserId, organizationId, membershipId, "MembershipRejected", reason, cancellationToken);
    }

    public async Task PromoteAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken = default)
    {
        Organization organization = await LoadForAdministrationAsync(administratorUserId, organizationId, cancellationToken);
        organization.PromoteToAdministrator(membershipId);
        OrganizationMembership membership = organization.Memberships.Single(item => item.Id == membershipId);
        AddMembershipChangedOutbox(organizationId, membership, "PromotedToAdministrator", null);
        await SaveAndAuditAsync(administratorUserId, organizationId, membershipId, "MembershipPromotedToAdministrator", null, cancellationToken);
    }

    public async Task DemoteAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken = default)
    {
        if (administratorUserId == await GetMembershipUserIdAsync(organizationId, membershipId, cancellationToken))
        {
            throw new InvalidOperationException("You cannot demote your own administrator membership. Promote another administrator first.");
        }

        Organization organization = await LoadForAdministrationAsync(administratorUserId, organizationId, cancellationToken);
        organization.DemoteAdministrator(membershipId);
        OrganizationMembership membership = organization.Memberships.Single(item => item.Id == membershipId);
        AddMembershipChangedOutbox(organizationId, membership, "DemotedToMember", null);
        await SaveAndAuditAsync(administratorUserId, organizationId, membershipId, "MembershipDemotedToMember", null, cancellationToken);
    }

    public async Task RevokeAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (administratorUserId == await GetMembershipUserIdAsync(organizationId, membershipId, cancellationToken))
        {
            throw new InvalidOperationException("You cannot revoke your own organization membership.");
        }

        Organization organization = await LoadForAdministrationAsync(administratorUserId, organizationId, cancellationToken);
        DateTimeOffset now = clock.UtcNow;
        organization.RevokeMembership(membershipId, administratorUserId, now, reason);
        OrganizationMembership membership = organization.Memberships.Single(item => item.Id == membershipId);

        List<AssistanceRequest> affectedRequests = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .Where(item => item.OrganizationId == organizationId && item.CreatedByUserId == membership.UserId)
            .ToListAsync(cancellationToken);

        foreach (AssistanceRequest request in affectedRequests)
            request.HoldForRepresentativeReview(administratorUserId, now, "Responsible organization representative was removed.");

        bool hasAnotherApprovedMembership = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(item => item.UserId == membership.UserId
                && item.Id != membership.Id
                && item.Status == MembershipStatus.Approved, cancellationToken);

        if (!hasAnotherApprovedMembership)
        {
            var user = await dbContext.Users.SingleAsync(item => item.Id == membership.UserId, cancellationToken);
            user.IndividualProfileUpdateRequired = true;
            user.IndividualProfileUpdateRequiredUtc = now;
        }

        AddMembershipChangedOutbox(organizationId, membership, "Revoked", reason);
        OrganizationMembershipEndedMessage ended = new(
            Guid.NewGuid(), now, organizationId, membership.Id, membership.UserId,
            administratorUserId, reason, affectedRequests
                .Where(item => item.Status == RequestStatus.RepresentativeReviewRequired)
                .Select(item => item.Id).ToArray());
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = ended.MessageId,
            MessageType = typeof(OrganizationMembershipEndedMessage).FullName ?? nameof(OrganizationMembershipEndedMessage),
            Payload = System.Text.Json.JsonSerializer.Serialize(ended),
            OccurredUtc = ended.OccurredUtc,
            AttemptCount = 0
        });
        await SaveAndAuditAsync(administratorUserId, organizationId, membershipId, "MembershipRevoked", reason, cancellationToken);
    }

    public async Task ContinueAffectedRequestAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        await EnsureAdministratorAsync(administratorUserId, organizationId, cancellationToken);
        AssistanceRequest request = await dbContext.AssistanceRequests
            .Include(item => item.StatusHistory)
            .SingleAsync(item => item.Id == requestId && item.OrganizationId == organizationId, cancellationToken);
        request.ContinueAfterRepresentativeReview(administratorUserId, clock.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(administratorUserId, "RepresentativeReviewContinued",
            "AssistanceRequest", requestId, organizationId, null, cancellationToken);
    }

    private async Task<Organization> LoadForAdministrationAsync(
        Guid administratorUserId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        Organization organization = await dbContext.Organizations
            .Include(item => item.Memberships)
            .SingleAsync(item => item.Id == organizationId, cancellationToken);

        bool isAdministrator = organization.Memberships.Any(
            membership => membership.UserId == administratorUserId
                && membership.Status == MembershipStatus.Approved
                && membership.Role == OrganizationRole.Administrator);

        if (!isAdministrator)
        {
            throw new UnauthorizedAccessException("Organization administrator access is required.");
        }

        return organization;
    }

    private async Task EnsureAdministratorAsync(
        Guid administratorUserId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        bool isAdministrator = await dbContext.OrganizationMemberships
            .AsNoTracking()
            .AnyAsync(
                membership => membership.OrganizationId == organizationId
                    && membership.UserId == administratorUserId
                    && membership.Status == MembershipStatus.Approved
                    && membership.Role == OrganizationRole.Administrator,
                cancellationToken);

        if (!isAdministrator)
        {
            throw new UnauthorizedAccessException("Organization administrator access is required.");
        }
    }

    private Task<Guid> GetMembershipUserIdAsync(
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken)
    {
        return dbContext.OrganizationMemberships
            .AsNoTracking()
            .Where(item => item.OrganizationId == organizationId && item.Id == membershipId)
            .Select(item => item.UserId)
            .SingleAsync(cancellationToken);
    }

    private void AddMembershipChangedOutbox(
        Guid organizationId,
        OrganizationMembership membership,
        string change,
        string? detail)
    {
        MembershipChangedMessage message = new(
            Guid.NewGuid(),
            clock.UtcNow,
            organizationId,
            membership.Id,
            membership.UserId,
            change,
            detail);

        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = message.MessageId,
            MessageType = typeof(MembershipChangedMessage).FullName ?? nameof(MembershipChangedMessage),
            Payload = System.Text.Json.JsonSerializer.Serialize(message),
            OccurredUtc = message.OccurredUtc,
            AttemptCount = 0
        });
    }

    private async Task SaveAndAuditAsync(
        Guid actorUserId,
        Guid organizationId,
        Guid membershipId,
        string action,
        string? detail,
        CancellationToken cancellationToken)
    {
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            actorUserId,
            action,
            "OrganizationMembership",
            membershipId,
            organizationId,
            detail,
            cancellationToken);
    }
}
