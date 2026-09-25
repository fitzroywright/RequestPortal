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

    public async Task<ExternalReviewerInvitationView> InviteAsync(
        Guid invitedByUserId,
        string email,
        string displayName,
        string scope,
        DateTimeOffset expiresUtc,
        string publicBaseUrl,
        CancellationToken cancellationToken = default)
    {
        if (!ExternalReviewerScopes.IsSupported(scope))
        {
            throw new ArgumentException("Unsupported external reviewer scope.", nameof(scope));
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
            expiresUtc);

        dbContext.ExternalReviewerInvitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);

        string baseUrl = publicBaseUrl.TrimEnd('/');
        string link = $"{baseUrl}/ExternalReview/Authenticate?id={invitation.Id:D}&token={Uri.EscapeDataString(token)}";
        await notificationSender.SendAsync(
            new NotificationEnvelope(
                invitation.Email,
                "Request Portal external review invitation",
                $"You have been invited to a temporary, read-only Request Portal review session. This invitation expires {invitation.ExpiresUtc:u}.\n\nOpen this secure link: {link}\n\nIf you were not expecting this invitation, do not use the link.",
                CorrelationId: invitation.Id.ToString("D")),
            cancellationToken);

        await auditWriter.WriteAsync(
            invitedByUserId,
            "ExternalReviewerInvited",
            "ExternalReviewerInvitation",
            invitation.Id,
            null,
            $"email={invitation.Email}; scope={invitation.Scope}; expires={invitation.ExpiresUtc:O}",
            cancellationToken);

        return ToView(invitation);
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
        await auditWriter.WriteAsync(
            Guid.Empty,
            "ExternalReviewerAuthenticated",
            "ExternalReviewerInvitation",
            invitation.Id,
            null,
            $"email={invitation.Email}; scope={invitation.Scope}",
            cancellationToken);

        return new ExternalReviewerAuthentication(
            invitation.Id,
            invitation.Email,
            invitation.DisplayName,
            invitation.Scope,
            invitation.ExpiresUtc);
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
        await auditWriter.WriteAsync(
            Guid.Empty,
            "ExternalReviewerAccessed",
            "ExternalReviewerInvitation",
            invitation.Id,
            null,
            $"email={invitation.Email}; scope={invitation.Scope}",
            cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<ExternalReviewerInvitationView>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await dbContext.ExternalReviewerInvitations
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedUtc)
            .Select(item => new ExternalReviewerInvitationView(
                item.Id,
                item.Email,
                item.DisplayName,
                item.Scope,
                item.CreatedUtc,
                item.ExpiresUtc,
                item.RedeemedUtc,
                item.RevokedUtc,
                item.LastAccessUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task RevokeAsync(Guid invitationId, Guid revokedByUserId, CancellationToken cancellationToken = default)
    {
        ExternalReviewerInvitation invitation = await dbContext.ExternalReviewerInvitations
            .SingleAsync(item => item.Id == invitationId, cancellationToken);
        invitation.Revoke(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            revokedByUserId,
            "ExternalReviewerRevoked",
            "ExternalReviewerInvitation",
            invitation.Id,
            null,
            $"email={invitation.Email}; scope={invitation.Scope}",
            cancellationToken);
    }

    public async Task<ExternalReviewerInvitationView> ReissueAsync(
        Guid invitationId,
        Guid invitedByUserId,
        string publicBaseUrl,
        CancellationToken cancellationToken = default)
    {
        ExternalReviewerInvitation existing = await dbContext.ExternalReviewerInvitations
            .SingleAsync(item => item.Id == invitationId, cancellationToken);

        existing.Revoke(DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        await auditWriter.WriteAsync(
            invitedByUserId,
            "ExternalReviewerReissued",
            "ExternalReviewerInvitation",
            existing.Id,
            null,
            $"email={existing.Email}; scope={existing.Scope}",
            cancellationToken);

        return await InviteAsync(
            invitedByUserId,
            existing.Email,
            existing.DisplayName,
            existing.Scope,
            DateTimeOffset.UtcNow.AddHours(24),
            publicBaseUrl,
            cancellationToken);
    }

    private static ExternalReviewerInvitationView ToView(ExternalReviewerInvitation item) =>
        new(item.Id, item.Email, item.DisplayName, item.Scope, item.CreatedUtc, item.ExpiresUtc, item.RedeemedUtc, item.RevokedUtc, item.LastAccessUtc);

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
