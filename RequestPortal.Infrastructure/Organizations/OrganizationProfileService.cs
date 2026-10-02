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
            organization.TaxRegistrationNumber,
            organization.OrganizationType,
            organization.TradingName,
            organization.DateEstablished,
            organization.CountryOfRegistration,
            organization.Address,
            organization.Parish,
            organization.DistrictArea,
            organization.MailingAddress,
            organization.Phone,
            organization.AlternativePhone,
            organization.Email,
            organization.Website,
            organization.PrimaryContactName,
            organization.PrimaryContactTitle,
            organization.AuthorizedSignatoryName,
            organization.AuthorizedSignatoryTitle,
            organization.SecondaryContactName,
            organization.SecondaryContactEmail,
            organization.SecondaryContactPhone,
            organization.EmployeeCount,
            organization.VolunteerCount,
            organization.ApproximateBeneficiariesServed,
            organization.GeographicServiceArea,
            organization.MainActivities,
            organization.PopulationGroupsServed,
            organization.PreviouslyReceivedFfpAssistance,
            organization.AnnualOperatingBudget,
            organization.PrimaryFundingSources,
            organization.PremisesSituation,
            organization.RegistrationExpiryDate);
    }

    public async Task UpdateAsync(
        Guid userId,
        Guid organizationId,
        OrganizationProfileUpdate update,
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
            update.RegistrationNumber,
            update.TaxRegistrationNumber,
            update.OrganizationType,
            update.TradingName,
            update.DateEstablished,
            update.CountryOfRegistration,
            update.Address,
            update.Parish,
            update.DistrictArea,
            update.MailingAddress,
            update.Phone,
            update.AlternativePhone,
            update.Email,
            update.Website,
            update.PrimaryContactName,
            update.PrimaryContactTitle,
            update.AuthorizedSignatoryName,
            update.AuthorizedSignatoryTitle,
            update.SecondaryContactName,
            update.SecondaryContactEmail,
            update.SecondaryContactPhone,
            update.EmployeeCount,
            update.VolunteerCount,
            update.ApproximateBeneficiariesServed,
            update.GeographicServiceArea,
            update.MainActivities,
            update.PopulationGroupsServed,
            update.PreviouslyReceivedFfpAssistance,
            update.AnnualOperatingBudget,
            update.PrimaryFundingSources,
            update.PremisesSituation,
            update.RegistrationExpiryDate);

        await dbContext.SaveChangesAsync(cancellationToken);
    }}
