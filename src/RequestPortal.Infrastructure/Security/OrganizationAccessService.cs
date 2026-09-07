using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Security;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Security;

public sealed class OrganizationAccessService : IOrganizationAccessService
{
    private readonly RequestPortalDbContext dbContext;

    public OrganizationAccessService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public Task<bool> IsApprovedMemberAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.OrganizationMemberships.AnyAsync(
            membership =>
                membership.UserId == userId &&
                membership.OrganizationId == organizationId &&
                membership.Status == MembershipStatus.Approved,
            cancellationToken);
    }

    public Task<bool> IsAdministratorAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.OrganizationMemberships.AnyAsync(
            membership =>
                membership.UserId == userId &&
                membership.OrganizationId == organizationId &&
                membership.Status == MembershipStatus.Approved &&
                membership.Role == OrganizationRole.Administrator,
            cancellationToken);
    }
}
