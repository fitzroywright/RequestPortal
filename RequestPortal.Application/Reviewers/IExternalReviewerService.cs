using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RequestPortal.Application.Reviewers;

public static class ExternalReviewerScopes
{
    public const string ManagementDashboard = "ManagementDashboard";
    public const string DeliverySummary = "DeliverySummary";
    public const string OrganizationPrefix = "Organization:";
    public const string RequestPrefix = "Request:";
    public const string DateRangePrefix = "DateRange:";

    public static bool IsSupported(string scope)
    {
        if (string.Equals(scope, ManagementDashboard, StringComparison.Ordinal) ||
            string.Equals(scope, DeliverySummary, StringComparison.Ordinal))
        {
            return true;
        }

        if (scope.StartsWith(OrganizationPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(scope[OrganizationPrefix.Length..], out _))
        {
            return true;
        }

        if (scope.StartsWith(RequestPrefix, StringComparison.Ordinal) &&
            Guid.TryParse(scope[RequestPrefix.Length..], out _))
        {
            return true;
        }

        if (scope.StartsWith(DateRangePrefix, StringComparison.Ordinal))
        {
            string[] parts = scope[DateRangePrefix.Length..].Split('|', 2);
            return parts.Length == 2 &&
                   DateTimeOffset.TryParse(parts[0], out _) &&
                   DateTimeOffset.TryParse(parts[1], out _);
        }

        return false;
    }
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
    Task<ExternalReviewerInvitationView> ReissueAsync(Guid invitationId, Guid invitedByUserId, string publicBaseUrl, CancellationToken cancellationToken = default);
}
