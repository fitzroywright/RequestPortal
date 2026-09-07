using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Administration;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Administration;

public sealed class StaffOrganizationService : IStaffOrganizationService
{
    private readonly RequestPortalDbContext dbContext;

    public StaffOrganizationService(RequestPortalDbContext dbContext)
    {
        this.dbContext = dbContext;
    }

    public async Task<IReadOnlyList<StaffOrganizationSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        List<StaffOrganizationSummary> organizations = await dbContext.Organizations
            .AsNoTracking()
            .OrderBy(item => item.Name)
            .Select(item => new StaffOrganizationSummary(
                item.Id,
                item.Name,
                item.CountryCode,
                item.Status.ToString(),
                item.RegistrationNumber,
                dbContext.OrganizationMemberships.Count(membership =>
                    membership.OrganizationId == item.Id && membership.Status == MembershipStatus.Approved),
                dbContext.AssistanceRequests.Count(request => request.OrganizationId == item.Id)))
            .ToListAsync(cancellationToken);

        return organizations;
    }

    public async Task<StaffOrganizationDetail?> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == organizationId, cancellationToken);

        if (organization is null)
        {
            return null;
        }

        List<StaffOrganizationMember> members = await (
            from membership in dbContext.OrganizationMemberships.AsNoTracking()
            join user in dbContext.Users.AsNoTracking() on membership.UserId equals user.Id
            where membership.OrganizationId == organizationId
            orderby user.DisplayName
            select new StaffOrganizationMember(
                user.Id,
                user.DisplayName,
                user.Email ?? string.Empty,
                membership.Status.ToString(),
                membership.Role.ToString()))
            .ToListAsync(cancellationToken);

        List<StaffOrganizationRequest> requests = await dbContext.AssistanceRequests
            .AsNoTracking()
            .Where(request => request.OrganizationId == organizationId)
            .OrderByDescending(request => request.CreatedUtc)
            .Select(request => new StaffOrganizationRequest(
                request.Id,
                request.PublicReference,
                request.Status.ToString(),
                request.Summary,
                request.CreatedUtc))
            .ToListAsync(cancellationToken);

        List<StaffAuditEntry> audit = await dbContext.AuditEntries
            .AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderByDescending(entry => entry.OccurredUtc)
            .Take(100)
            .Select(entry => new StaffAuditEntry(
                entry.Action,
                entry.EntityType,
                entry.EntityId,
                entry.ActorUserId,
                entry.OccurredUtc,
                entry.Detail))
            .ToListAsync(cancellationToken);

        return new StaffOrganizationDetail(
            organization.Id,
            organization.Name,
            organization.CountryCode,
            organization.Status.ToString(),
            organization.RegistrationNumber,
            organization.OrganizationType,
            organization.Address,
            organization.Phone,
            organization.Email,
            organization.PrimaryContactName,
            organization.RegistrationExpiryDate,
            organization.CreatedUtc,
            organization.ApprovedUtc,
            members,
            requests,
            audit);
    }
}
