using System;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Organizations;

public sealed record OrganizationProfile(
    Guid Id,
    string Name,
    string CountryCode,
    string Status,
    string? RegistrationNumber,
    string? TaxRegistrationNumber,
    string? OrganizationType,
    string? TradingName,
    DateOnly? DateEstablished,
    string? CountryOfRegistration,
    string? Address,
    string? Parish,
    string? DistrictArea,
    string? MailingAddress,
    string? Phone,
    string? AlternativePhone,
    string? Email,
    string? Website,
    string? PrimaryContactName,
    string? PrimaryContactTitle,
    string? AuthorizedSignatoryName,
    string? AuthorizedSignatoryTitle,
    string? SecondaryContactName,
    string? SecondaryContactEmail,
    string? SecondaryContactPhone,
    int? EmployeeCount,
    int? VolunteerCount,
    int? ApproximateBeneficiariesServed,
    string? GeographicServiceArea,
    string? MainActivities,
    string? PopulationGroupsServed,
    bool? PreviouslyReceivedFfpAssistance,
    decimal? AnnualOperatingBudget,
    string? PrimaryFundingSources,
    string? PremisesSituation,
    string? ReferralSource,
    DateOnly? RegistrationExpiryDate);

public sealed record OrganizationProfileUpdate(
    string? RegistrationNumber,
    string? TaxRegistrationNumber,
    string? OrganizationType,
    string? TradingName,
    DateOnly? DateEstablished,
    string? CountryOfRegistration,
    string? Address,
    string? Parish,
    string? DistrictArea,
    string? MailingAddress,
    string? Phone,
    string? AlternativePhone,
    string? Email,
    string? Website,
    string? PrimaryContactName,
    string? PrimaryContactTitle,
    string? AuthorizedSignatoryName,
    string? AuthorizedSignatoryTitle,
    string? SecondaryContactName,
    string? SecondaryContactEmail,
    string? SecondaryContactPhone,
    int? EmployeeCount,
    int? VolunteerCount,
    int? ApproximateBeneficiariesServed,
    string? GeographicServiceArea,
    string? MainActivities,
    string? PopulationGroupsServed,
    bool? PreviouslyReceivedFfpAssistance,
    decimal? AnnualOperatingBudget,
    string? PrimaryFundingSources,
    string? PremisesSituation,
    string? ReferralSource,
    DateOnly? RegistrationExpiryDate);

public interface IOrganizationProfileService
{
    Task<OrganizationProfile?> GetAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        Guid userId,
        Guid organizationId,
        OrganizationProfileUpdate update,
        CancellationToken cancellationToken = default);
}
