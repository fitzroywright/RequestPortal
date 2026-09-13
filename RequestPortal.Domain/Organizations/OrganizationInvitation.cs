using System;

namespace RequestPortal.Domain.Organizations;

public enum InvitationStatus { Pending = 0, Accepted = 1, Revoked = 2, Expired = 3 }

public sealed class OrganizationInvitation
{
    private OrganizationInvitation() { }
    public OrganizationInvitation(Guid id, Guid organizationId, string email, string tokenHash, Guid invitedByUserId, DateTimeOffset createdUtc, DateTimeOffset expiresUtc)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(tokenHash)) throw new ArgumentException("Invitation email and token are required.");
        Id = id; OrganizationId = organizationId; Email = email.Trim().ToLowerInvariant(); TokenHash = tokenHash; InvitedByUserId = invitedByUserId; CreatedUtc = createdUtc; ExpiresUtc = expiresUtc; Status = InvitationStatus.Pending;
    }
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string TokenHash { get; private set; } = string.Empty;
    public Guid InvitedByUserId { get; private set; }
    public DateTimeOffset CreatedUtc { get; private set; }
    public DateTimeOffset ExpiresUtc { get; private set; }
    public InvitationStatus Status { get; private set; }
    public Guid? AcceptedByUserId { get; private set; }
    public DateTimeOffset? AcceptedUtc { get; private set; }
    public void Accept(Guid userId, DateTimeOffset utcNow) { if (Status != InvitationStatus.Pending || utcNow > ExpiresUtc) throw new InvalidOperationException("Invitation is no longer valid."); Status = InvitationStatus.Accepted; AcceptedByUserId = userId; AcceptedUtc = utcNow; }
    public void Revoke() { if (Status == InvitationStatus.Pending) Status = InvitationStatus.Revoked; }
}
