using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Application.Reviewers;
using RequestPortal.Common.Messaging;
using RequestPortal.Domain.Reviewers;
using RequestPortal.Infrastructure.Persistence;

namespace RequestPortal.Infrastructure.Reviewers;

public sealed class ExternalReviewerService : IExternalReviewerService
{
    private readonly RequestPortalDbContext dbContext;
    private readonly INotificationSender notificationSender;
    private readonly IBusinessAuditWriter auditWriter;

    public ExternalReviewerService(
        RequestPortalDbContext dbContext,
        INotificationSender notificationSender,
        IBusinessAuditWriter auditWriter)
    {
        this.dbContext = dbContext;
        this.notificationSender = notificationSender;
        this.auditWriter = auditWriter;
    }

    public Task<ExternalReviewerInvitationView> InviteAsync(
        Guid invitedByUserId,
        string email,
        string displayName,
        string scope,
        DateTimeOffset expiresUtc,
        string publicBaseUrl,
        CancellationToken cancellationToken = default) =>
        InviteScopedAsync(
            invitedByUserId,
            email,
            displayName,
            scope,
            expiresUtc,
            publicBaseUrl,
            null,
            null,
            null,
            null,
            cancellationToken);

    public async Task<ExternalReviewerInvitationView> InviteScopedAsync(
        Guid invitedByUserId,
        string email,
        string displayName,
        string scope,
        DateTimeOffset expiresUtc,
        string publicBaseUrl,
        Guid? organizationId,
        Guid? requestId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateScope(scope, organizationId, requestId, fromUtc, toUtc);

        if (organizationId.HasValue)
        {
            bool organizationExists = await dbContext.Organizations
                .AsNoTracking()
                .AnyAsync(item => item.Id == organizationId.Value, cancellationToken);
            if (!organizationExists) throw new InvalidOperationException("Reviewer organization scope was not found.");
        }

        if (requestId.HasValue)
        {
            var requestScope = await dbContext.AssistanceRequests
                .AsNoTracking()
                .Where(item => item.Id == requestId.Value)
                .Select(item => new { item.Id, item.OrganizationId })
                .SingleOrDefaultAsync(cancellationToken);
            if (requestScope is null) throw new InvalidOperationException("Reviewer request scope was not found.");
            if (organizationId.HasValue && requestScope.OrganizationId != organizationId.Value)
                throw new InvalidOperationException("Reviewer request scope does not belong to the selected organization.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        ExternalReviewerInvitation invitation = new(
            Guid.NewGuid(),
            email,
            displayName,
            scope,
            HashToken(token),
            invitedByUserId,
            now,
            expiresUtc,
            organizationId,
            requestId,
            fromUtc,
            toUtc);

        dbContext.ExternalReviewerInvitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);

        await SendInvitationAsync(invitation, token, publicBaseUrl, cancellationToken);
        await AuditAsync(invitation, invitedByUserId, "ExternalReviewerInvited", cancellationToken);
        return ToView(invitation);
    }

    public async Task<ExternalReviewerInvitationView> ReissueAsync(
        Guid invitationId,
        Guid invitedByUserId,
        string publicBaseUrl,
        DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default)
    {
        ExternalReviewerInvitation previous = await dbContext.ExternalReviewerInvitations
            .SingleAsync(item => item.Id == invitationId, cancellationToken);

        previous.Revoke(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);

        ExternalReviewerInvitationView replacement = await InviteScopedAsync(
            invitedByUserId,
            previous.Email,
            previous.DisplayName,
            previous.Scope,
            expiresUtc,
            publicBaseUrl,
            previous.OrganizationId,
            previous.RequestId,
            previous.FromUtc,
            previous.ToUtc,
            cancellationToken);

        await auditWriter.WriteAsync(
            invitedByUserId,
            "ExternalReviewerInvitationReissued",
            "ExternalReviewerInvitation",
            invitationId,
            null,
            $"replacement={replacement.Id:D}",
            cancellationToken);

        return replacement;
    }

    public async Task<ExternalReviewerAuthentication?> AuthenticateAsync(
        Guid invitationId,
        string token,
        CancellationToken cancellationToken = default)
    {
        ExternalReviewerInvitation? invitation = await dbContext.ExternalReviewerInvitations
            .SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (invitation is null || invitation.RedeemedUtc.HasValue || !invitation.IsActive(now) || !TokenMatches(invitation.TokenHash, token))
        {
            return null;
        }

        invitation.RecordRedemption(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(invitation, Guid.Empty, "ExternalReviewerAuthenticated", cancellationToken);

        return new ExternalReviewerAuthentication(
            invitation.Id,
            invitation.Email,
            invitation.DisplayName,
            invitation.Scope,
            invitation.ExpiresUtc,
            invitation.OrganizationId,
            invitation.RequestId,
            invitation.FromUtc,
            invitation.ToUtc);
    }

    public async Task<bool> ValidateSessionAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        ExternalReviewerInvitation? invitation = await dbContext.ExternalReviewerInvitations
            .SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (invitation is null || !invitation.IsActive(now))
        {
            return false;
        }

        invitation.RecordAccess(now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(invitation, Guid.Empty, "ExternalReviewerAccessed", cancellationToken);
        return true;
    }

    public async Task<ExternalReviewerInvitationView?> GetActiveSessionAsync(
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        ExternalReviewerInvitation? invitation = await dbContext.ExternalReviewerInvitations
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == invitationId, cancellationToken);
        return invitation is not null && invitation.IsActive(DateTimeOffset.UtcNow)
            ? ToView(invitation)
            : null;
    }

    public async Task<IReadOnlyList<ExternalReviewerInvitationView>> ListAsync(CancellationToken cancellationToken = default)
    {
        List<ExternalReviewerInvitation> items = await dbContext.ExternalReviewerInvitations
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedUtc)
            .ToListAsync(cancellationToken);
        return items.Select(ToView).ToArray();
    }

    public async Task RevokeAsync(Guid invitationId, Guid revokedByUserId, CancellationToken cancellationToken = default)
    {
        ExternalReviewerInvitation invitation = await dbContext.ExternalReviewerInvitations
            .SingleAsync(item => item.Id == invitationId, cancellationToken);
        invitation.Revoke(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(invitation, revokedByUserId, "ExternalReviewerRevoked", cancellationToken);
    }

    private async Task SendInvitationAsync(
        ExternalReviewerInvitation invitation,
        string token,
        string publicBaseUrl,
        CancellationToken cancellationToken)
    {
        string baseUrl = publicBaseUrl.TrimEnd('/');
        string link = $"{baseUrl}/ExternalReview/Authenticate?id={invitation.Id:D}&token={Uri.EscapeDataString(token)}";
        await notificationSender.SendAsync(
            new NotificationEnvelope(
                invitation.Email,
                "Request Portal external review invitation",
                $"You have been invited to a temporary, read-only Request Portal review session. This invitation expires {invitation.ExpiresUtc:u}.\n\nScope: {ScopeDescription(invitation)}\n\nOpen this secure one-time link: {link}\n\nIf you were not expecting this invitation, do not use the link.",
                CorrelationId: invitation.Id.ToString("D")),
            cancellationToken);
    }

    private Task AuditAsync(
        ExternalReviewerInvitation invitation,
        Guid actorUserId,
        string action,
        CancellationToken cancellationToken) =>
        auditWriter.WriteAsync(
            actorUserId,
            action,
            "ExternalReviewerInvitation",
            invitation.Id,
            invitation.OrganizationId,
            $"email={invitation.Email}; scope={ScopeDescription(invitation)}; expires={invitation.ExpiresUtc:O}",
            cancellationToken);

    private static ExternalReviewerInvitationView ToView(ExternalReviewerInvitation item) =>
        new(
            item.Id,
            item.Email,
            item.DisplayName,
            item.Scope,
            item.CreatedUtc,
            item.ExpiresUtc,
            item.RedeemedUtc,
            item.RevokedUtc,
            item.LastAccessUtc,
            item.InvitedByUserId,
            item.OrganizationId,
            item.RequestId,
            item.FromUtc,
            item.ToUtc);

    private static void ValidateScope(
        string scope,
        Guid? organizationId,
        Guid? requestId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        string[] supported =
        [
            ExternalReviewerScopes.ManagementDashboard,
            ExternalReviewerScopes.Organization,
            ExternalReviewerScopes.Request,
            ExternalReviewerScopes.DateRange
        ];

        if (!supported.Contains(scope, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported external reviewer scope.", nameof(scope));
        if (scope == ExternalReviewerScopes.Organization && !organizationId.HasValue)
            throw new ArgumentException("Organization scope requires an organization.", nameof(organizationId));
        if (scope == ExternalReviewerScopes.Request && !requestId.HasValue)
            throw new ArgumentException("Request scope requires a request.", nameof(requestId));
        if (scope == ExternalReviewerScopes.DateRange && !fromUtc.HasValue && !toUtc.HasValue)
            throw new ArgumentException("Date range scope requires a start or end date.");
        if (fromUtc.HasValue && toUtc.HasValue && toUtc.Value < fromUtc.Value)
            throw new ArgumentException("Reviewer date scope end must not be earlier than its start.");
    }

    private static string ScopeDescription(ExternalReviewerInvitation item)
    {
        List<string> parts = [item.Scope];
        if (item.OrganizationId.HasValue) parts.Add($"organization={item.OrganizationId:D}");
        if (item.RequestId.HasValue) parts.Add($"request={item.RequestId:D}");
        if (item.FromUtc.HasValue) parts.Add($"from={item.FromUtc:O}");
        if (item.ToUtc.HasValue) parts.Add($"to={item.ToUtc:O}");
        return string.Join("; ", parts);
    }

    private static bool TokenMatches(string expectedHash, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        byte[] expected = Convert.FromHexString(expectedHash);
        byte[] actual = Convert.FromHexString(HashToken(token));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
