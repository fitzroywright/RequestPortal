using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Portal;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Portal;

public sealed class StaffOrganizationDirectoryService : IStaffOrganizationDirectoryService
{
    private readonly RequestPortalDbContext dbContext;

    public StaffOrganizationDirectoryService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<IReadOnlyList<StaffOrganizationSummary>> GetAllOrganizationsAsync(
        CancellationToken cancellationToken = default)
    {
        List<StaffOrganizationSummary> organizations = await dbContext.Organizations
            .AsNoTracking()
            .Where(organization => organization.OrganizationType != "IndividualContext")
            .OrderBy(organization => organization.Name)
            .Select(organization => new StaffOrganizationSummary(
                organization.Id,
                organization.Name,
                organization.CountryCode,
                organization.Status.ToString(),
                organization.RegistrationNumber,
                dbContext.OrganizationMemberships.Count(membership =>
                    membership.OrganizationId == organization.Id &&
                    membership.Status == MembershipStatus.Approved),
                dbContext.AssistanceRequests.Count(request => request.OrganizationId == organization.Id),
                organization.CreatedUtc,
                organization.ApprovedUtc))
            .ToListAsync(cancellationToken);

        return organizations;
    }
}
