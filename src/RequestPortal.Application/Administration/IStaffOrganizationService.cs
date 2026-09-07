using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Administration;

public sealed record StaffOrganizationSummary(
    Guid Id,
    string Name,
    string CountryCode,
    string Status,
    string? RegistrationNumber,
    int ApprovedMemberCount,
    int RequestCount);

public sealed record StaffOrganizationDetail(
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
    DateOnly? RegistrationExpiryDate,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ApprovedUtc,
    IReadOnlyList<StaffOrganizationMember> Members,
    IReadOnlyList<StaffOrganizationRequest> Requests,
    IReadOnlyList<StaffAuditEntry> Audit);

public sealed record StaffOrganizationMember(
    Guid UserId,
    string DisplayName,
    string Email,
    string MembershipStatus,
    string Role);

public sealed record StaffOrganizationRequest(
    Guid RequestId,
    string PublicReference,
    string Status,
    string? Summary,
    DateTimeOffset CreatedUtc);

public sealed record StaffAuditEntry(
    string Action,
    string EntityType,
    Guid EntityId,
    Guid ActorUserId,
    DateTimeOffset OccurredUtc,
    string? Detail);

public interface IStaffOrganizationService
{
    Task<IReadOnlyList<StaffOrganizationSummary>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<StaffOrganizationDetail?> GetAsync(Guid organizationId, CancellationToken cancellationToken = default);

    Task RejectAsync(Guid staffUserId, Guid organizationId, string reason, CancellationToken cancellationToken = default);

    Task SuspendAsync(Guid staffUserId, Guid organizationId, string reason, CancellationToken cancellationToken = default);

    Task ReactivateAsync(Guid staffUserId, Guid organizationId, string? notes, CancellationToken cancellationToken = default);
}
