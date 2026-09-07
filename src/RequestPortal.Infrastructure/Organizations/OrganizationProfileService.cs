using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Organizations;
using RequestPortal.Application.Security;
using RequestPortal.Domain.Organizations;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Organizations;

public sealed class OrganizationProfileService : IOrganizationProfileService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly IOrganizationAccessService organizationAccessService;

    public OrganizationProfileService(
        RequestPortalDbContext dbContext,
        IOrganizationAccessService organizationAccessService)
    {
        this.dbContext = dbContext;
        this.organizationAccessService = organizationAccessService;
    }

    public async Task<OrganizationProfile?> GetAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        Organization? organization = await dbContext.Organizations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == organizationId, cancellationToken);

        if (organization is null)
        {
            return null;
        }

        bool isApprovedMember = await organizationAccessService.IsApprovedMemberAsync(userId, organizationId, cancellationToken);
        if (organization.RegisteredByUserId != userId && !isApprovedMember)
        {
            throw new UnauthorizedAccessException("The current user cannot access this organization profile.");
        }

        return new OrganizationProfile(
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
            organization.RegistrationExpiryDate);
    }

    public async Task UpdateAsync(
        Guid userId,
        Guid organizationId,
        string? registrationNumber,
        string? organizationType,
        string? address,
        string? phone,
        string? email,
        string? primaryContactName,
        DateOnly? registrationExpiryDate,
        CancellationToken cancellationToken = default)
    {
        Organization organization = await dbContext.Organizations
            .SingleAsync(item => item.Id == organizationId, cancellationToken);

        bool isAdministrator = await organizationAccessService.IsAdministratorAsync(userId, organizationId, cancellationToken);
        if (organization.RegisteredByUserId != userId && !isAdministrator)
        {
            throw new UnauthorizedAccessException("Only the registering representative or an organization administrator may edit this profile.");
        }

        organization.UpdateProfile(
            registrationNumber,
            organizationType,
            address,
            phone,
            email,
            primaryContactName,
            registrationExpiryDate);

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
