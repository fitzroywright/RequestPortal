using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Beneficiaries;

public sealed record BeneficiarySummary(
    Guid Id,
    Guid OrganizationId,
    string FirstName,
    string LastName,
    DateOnly? DateOfBirth,
    string? ParentOrGuardian,
    string? Location);

public interface IBeneficiaryService
{
    Task<Guid> CreateAsync(
        Guid userId,
        Guid organizationId,
        string firstName,
        string lastName,
        DateOnly? dateOfBirth,
        string? parentOrGuardian,
        string? location,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BeneficiarySummary>> GetForOrganizationAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default);
}
