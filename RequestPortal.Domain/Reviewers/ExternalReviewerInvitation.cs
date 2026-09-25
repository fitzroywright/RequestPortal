using System;

namespace RequestPortal.Domain.Reviewers;

public sealed class ExternalReviewerInvitation
{
    private ExternalReviewerInvitation() { }

    public ExternalReviewerInvitation(
        Guid id,
        string email,
        string displayName,
        string scope,
        string tokenHash,
        Guid invitedByUserId,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc)
    {
        if (id == Guid.Empty) throw new ArgumentException("Invitation id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Reviewer email is required.", nameof(email));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Reviewer name is required.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("Reviewer scope is required.", nameof(scope));
        if (string.IsNullOrWhiteSpace(tokenHash)) throw new ArgumentException("Token hash is required.", nameof(tokenHash));
        if (expiresUtc <= createdUtc) throw new ArgumentException("Reviewer access must expire after it is created.", nameof(expiresUtc));

        Id = id;
        Email = email.Trim().ToLowerInvariant();
        DisplayName = displayName.Trim();
        Scope = scope.Trim();
        TokenHash = tokenHash;
        InvitedByUserId = invitedByUserId;
        CreatedUtc = createdUtc;
        ExpiresUtc = expiresUtc;
    }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public string Scope { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public Guid InvitedByUserId { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset ExpiresUtc { get; private set; }
    public DateTimeOffset? RedeemedUtc { get; private set; }
    public DateTimeOffset? RevokedUtc { get; private set; }
    public DateTimeOffset? LastAccessUtc { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedUtc is null && ExpiresUtc > now;

    public void RecordRedemption(DateTimeOffset now)
    {
        if (!IsActive(now)) throw new InvalidOperationException("Reviewer invitation is expired or revoked.");
        RedeemedUtc ??= now;
        LastAccessUtc = now;
    }

    public void RecordAccess(DateTimeOffset now)
    {
        if (!IsActive(now)) throw new InvalidOperationException("Reviewer access is expired or revoked.");
        LastAccessUtc = now;
    }

    public void Revoke(DateTimeOffset now)
    {
        RevokedUtc ??= now;
    }
}
