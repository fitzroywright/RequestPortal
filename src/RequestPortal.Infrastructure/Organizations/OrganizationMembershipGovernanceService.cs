using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Organizations;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Persistence;

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
        organization.RevokeMembership(membershipId, administratorUserId, clock.UtcNow, reason);
        await SaveAndAuditAsync(administratorUserId, organizationId, membershipId, "MembershipRevoked", reason, cancellationToken);
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
