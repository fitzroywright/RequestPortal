using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Portal;

public sealed record OrganizationSummary(
    Guid Id,
    string Name,
    string CountryCode,
    string OrganizationStatus,
    string MembershipStatus,
    string Role);

public sealed record PendingMembershipSummary(
    Guid MembershipId,
    Guid OrganizationId,
    Guid UserId,
    string UserDisplayName,
    string UserEmail,
    DateTimeOffset RequestedUtc);

public sealed record RequestSummary(
    Guid Id,
    string PublicReference,
    Guid OrganizationId,
    string OrganizationName,
    string Status,
    string? Summary,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? SubmittedUtc);

public sealed record RequestDetail(
    Guid Id,
    string PublicReference,
    Guid OrganizationId,
    string OrganizationName,
    string Status,
    string? Summary,
    IReadOnlyList<RequestItemDetail> Items,
    IReadOnlyList<RequestHistoryDetail> History);

public sealed record RequestItemDetail(
    Guid Id,
    string Description,
    decimal Quantity,
    Guid UnitOfMeasureId,
    string UnitOfMeasureCode,
    string UnitOfMeasureName);

public sealed record RequestHistoryDetail(
    string Status,
    DateTimeOffset ChangedUtc,
    string? Reason);

public interface IPortalWorkflowService
{
    Task<IReadOnlyList<OrganizationSummary>> GetOrganizationsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrganizationSummary>> GetAvailableOrganizationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<Guid> RegisterOrganizationAsync(
        Guid userId,
        string name,
        string countryCode,
        CancellationToken cancellationToken = default);

    Task RequestMembershipAsync(
        Guid userId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrganizationSummary>> GetPendingOrganizationsAsync(
        CancellationToken cancellationToken = default);

    Task ApproveOrganizationAsync(
        Guid staffUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingMembershipSummary>> GetPendingMembershipsAsync(
        Guid administratorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task ApproveMembershipAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid membershipId,
        CancellationToken cancellationToken = default);

    Task<Guid> CreateRequestAsync(
        Guid userId,
        Guid organizationId,
        string summary,
        CancellationToken cancellationToken = default);

    Task AddRequestItemAsync(
        Guid userId,
        Guid requestId,
        string description,
        decimal quantity,
        Guid unitOfMeasureId,
        CancellationToken cancellationToken = default);

    Task SubmitRequestAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task ResubmitRequestAsync(
        Guid userId,
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RequestSummary>> GetRequestsForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RequestSummary>> GetSubmittedRequestsAsync(
        CancellationToken cancellationToken = default);

    Task<RequestDetail?> GetRequestAsync(
        Guid userId,
        Guid requestId,
        bool isStaff,
        CancellationToken cancellationToken = default);

    Task ApproveRequestAsync(
        Guid staffUserId,
        Guid requestId,
        CancellationToken cancellationToken = default);

    Task RejectRequestAsync(
        Guid staffUserId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default);

    Task RequestMoreInformationAsync(
        Guid staffUserId,
        Guid requestId,
        string reason,
        CancellationToken cancellationToken = default);
}
