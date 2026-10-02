using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Organizations;

public sealed record OrganizationMembershipView(
    Guid MembershipId,
    Guid OrganizationId,
    Guid UserId,
    string DisplayName,
    string Email,
    string Status,
    string Role,
    DateTimeOffset RequestedUtc,
    DateTimeOffset? ApprovedUtc,
    DateTimeOffset? EndedUtc,
    string? EndReason);

public interface IOrganizationMembershipGovernanceService
{
    Task<IReadOnlyList<OrganizationMembershipView>> GetMembershipsAsync(
        Guid administratorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task ApproveAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken = default);

    Task RejectAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        string reason,
        CancellationToken cancellationToken = default);

    Task PromoteAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken = default);

    Task DemoteAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken = default);

    Task ContinueAffectedRequestAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        string reason,
        CancellationToken cancellationToken = default);
}
