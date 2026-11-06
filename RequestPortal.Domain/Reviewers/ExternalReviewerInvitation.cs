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
        DateTimeOffset expiresUtc,
        Guid? organizationId = null,
        Guid? requestId = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Invitation id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(email)) throw new ArgumentException("Reviewer email is required.", nameof(email));
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Reviewer name is required.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(scope)) throw new ArgumentException("Reviewer scope is required.", nameof(scope));
        if (string.IsNullOrWhiteSpace(tokenHash)) throw new ArgumentException("Token hash is required.", nameof(tokenHash));
        if (expiresUtc <= createdUtc) throw new ArgumentException("Reviewer access must expire after it is created.", nameof(expiresUtc));

        Id = id;
        Email = email.Trim();
        DisplayName = displayName.Trim();
        Scope = scope.Trim();
        TokenHash = tokenHash;
        InvitedByUserId = invitedByUserId;
        CreatedUtc = createdUtc;
        ExpiresUtc = expiresUtc;
        OrganizationId = organizationId;
        RequestId = requestId;
        FromUtc = fromUtc;
        ToUtc = toUtc;

        if (fromUtc.HasValue && toUtc.HasValue && toUtc.Value < fromUtc.Value)
            throw new ArgumentException("Reviewer date scope end must not be earlier than its start.");
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
    public Guid? OrganizationId { get; private set; }
    public Guid? RequestId { get; private set; }
    public DateTimeOffset? FromUtc { get; private set; }
    public DateTimeOffset? ToUtc { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedUtc is null && ExpiresUtc > now;

    public void RecordRedemption(DateTimeOffset now)
    {
        if (!IsActive(now)) throw new InvalidOperationException("Reviewer invitation is expired or revoked.");
        if (RedeemedUtc.HasValue) throw new InvalidOperationException("Reviewer invitation link has already been used.");
        RedeemedUtc = now;
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
