using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Reviewers;

public static class ExternalReviewerScopes
{
    public const string ManagementDashboard = "ManagementDashboard";
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
    DateTimeOffset? LastAccessUtc);

public sealed record ExternalReviewerAuthentication(
    Guid InvitationId,
    string Email,
    string DisplayName,
    string Scope,
    DateTimeOffset ExpiresUtc);

public interface IExternalReviewerService
{
    Task<ExternalReviewerInvitationView> InviteAsync(Guid invitedByUserId, string email, string displayName, string scope, DateTimeOffset expiresUtc, string publicBaseUrl, CancellationToken cancellationToken = default);
    Task<ExternalReviewerAuthentication?> AuthenticateAsync(Guid invitationId, string token, CancellationToken cancellationToken = default);
    Task<bool> ValidateSessionAsync(Guid invitationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExternalReviewerInvitationView>> ListAsync(CancellationToken cancellationToken = default);
    Task RevokeAsync(Guid invitationId, Guid revokedByUserId, CancellationToken cancellationToken = default);
}
