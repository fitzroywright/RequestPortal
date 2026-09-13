using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Portal;

public sealed record StaffOrganizationSummary(
    Guid Id,
    string Name,
    string CountryCode,
    string Status,
    string? RegistrationNumber,
    int ApprovedMemberCount,
    int RequestCount,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ApprovedUtc);

public interface IStaffOrganizationDirectoryService
{
    Task<IReadOnlyList<StaffOrganizationSummary>> GetAllOrganizationsAsync(
        CancellationToken cancellationToken = default);
}
