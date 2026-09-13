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
    string? OrganizationType,
    string? Address,
    string? Phone,
    string? Email,
    string? PrimaryContactName,
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
        string? registrationNumber,
        string? organizationType,
        string? address,
        string? phone,
        string? email,
        string? primaryContactName,
        DateOnly? registrationExpiryDate,
        CancellationToken cancellationToken = default);
}
