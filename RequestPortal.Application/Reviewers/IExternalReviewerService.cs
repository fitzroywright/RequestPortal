using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Reviewers;

public static class ExternalReviewerScopes
{
    public const string ManagementDashboard = "ManagementDashboard";
    public const string Organization = "Organization";
    public const string Request = "Request";
    public const string DateRange = "DateRange";
}

public sealed record ExternalReviewerInvitationView(
    Guid Id,
    string Email,
    string DisplayName,
    string Scope,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc,
    DateTimeOffset? RedeemedUtc,
    DateTimeOffset? RevokedUtc,
    DateTimeOffset? LastAccessUtc,
    Guid InvitedByUserId,
    Guid? OrganizationId,
    Guid? RequestId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc);

public sealed record ExternalReviewerAuthentication(
    Guid InvitationId,
    string Email,
    string DisplayName,
    string Scope,
    DateTimeOffset ExpiresUtc,
    Guid? OrganizationId,
    Guid? RequestId,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc);

public interface IExternalReviewerService
{
    Task<ExternalReviewerInvitationView> InviteAsync(Guid invitedByUserId, string email, string displayName, string scope, DateTimeOffset expiresUtc, string publicBaseUrl, CancellationToken cancellationToken = default);
    Task<ExternalReviewerInvitationView> InviteScopedAsync(Guid invitedByUserId, string email, string displayName, string scope, DateTimeOffset expiresUtc, string publicBaseUrl, Guid? organizationId, Guid? requestId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, CancellationToken cancellationToken = default);
    Task<ExternalReviewerInvitationView> ReissueAsync(Guid invitationId, Guid invitedByUserId, string publicBaseUrl, DateTimeOffset expiresUtc, CancellationToken cancellationToken = default);
    Task<ExternalReviewerAuthentication?> AuthenticateAsync(Guid invitationId, string token, CancellationToken cancellationToken = default);
    Task<bool> ValidateSessionAsync(Guid invitationId, CancellationToken cancellationToken = default);
    Task<ExternalReviewerInvitationView?> GetActiveSessionAsync(Guid invitationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExternalReviewerInvitationView>> ListAsync(CancellationToken cancellationToken = default);
    Task RevokeAsync(Guid invitationId, Guid revokedByUserId, CancellationToken cancellationToken = default);
}
