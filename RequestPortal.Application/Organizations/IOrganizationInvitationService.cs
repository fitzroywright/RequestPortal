using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Organizations;

public sealed record OrganizationInvitationView(
    Guid Id,
    Guid OrganizationId,
    string OrganizationName,
    string Email,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    Guid InvitedByUserId,
    Guid? AcceptedByUserId,
    DateTimeOffset? AcceptedUtc);

public interface IOrganizationInvitationService
{
    Task<OrganizationInvitationView> InviteAsync(
        Guid administratorUserId,
        Guid organizationId,
        string email,
        string publicBaseUrl,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrganizationInvitationView>> GetForAdministratorAsync(
        Guid administratorUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrganizationInvitationView>> GetPendingForUserAsync(
        Guid userId,
        string? email,
        CancellationToken cancellationToken = default);

    Task AcceptAsync(
        Guid userId,
        string? userEmail,
        Guid invitationId,
        string token,
        CancellationToken cancellationToken = default);

    Task RevokeAsync(
        Guid administratorUserId,
        Guid organizationId,
        Guid invitationId,
        CancellationToken cancellationToken = default);
}
