using System;

namespace RequestPortal.Domain.Organizations;

public sealed class OrganizationMembership
{
    private OrganizationMembership()
    {
    }

    public OrganizationMembership(Guid id, Guid organizationId, Guid userId, DateTimeOffset requestedUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        UserId = userId;
        Status = MembershipStatus.Pending;
        Role = OrganizationRole.Member;
        RequestedUtc = requestedUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public Guid UserId { get; private set; }

    public MembershipStatus Status { get; private set; }

    public OrganizationRole Role { get; private set; }

    public DateTimeOffset RequestedUtc { get; private set; }

    public DateTimeOffset? ApprovedUtc { get; private set; }

    public Guid? ApprovedByUserId { get; private set; }

    public DateTimeOffset? EndedUtc { get; private set; }

    public Guid? EndedByUserId { get; private set; }

    public string? EndReason { get; private set; }

    public bool ReceiveOrganizationNotifications { get; private set; }

    public bool ReceiveRequestNotifications { get; private set; }

    public bool ReceiveMembershipNotifications { get; private set; }

    public void Approve(Guid approvedByUserId, DateTimeOffset approvedUtc)
    {
        EnsurePending();
        Status = MembershipStatus.Approved;
        ApprovedByUserId = approvedByUserId;
        ApprovedUtc = approvedUtc;
    }

    internal void ApproveAsAdministrator(Guid approvedByUserId, DateTimeOffset approvedUtc)
    {
        EnsurePending();
        Role = OrganizationRole.Administrator;
        ReceiveOrganizationNotifications = true;
        ReceiveRequestNotifications = true;
        ReceiveMembershipNotifications = true;
        Status = MembershipStatus.Approved;
        ApprovedByUserId = approvedByUserId;
        ApprovedUtc = approvedUtc;
    }

    public void Reject(Guid rejectedByUserId, DateTimeOffset rejectedUtc, string reason)
    {
        EnsurePending();
        Status = MembershipStatus.Rejected;
        EndedByUserId = rejectedByUserId;
        EndedUtc = rejectedUtc;
        EndReason = NormalizeReason(reason);
    }

    public void PromoteToAdministrator()
    {
        EnsureApproved();
        Role = OrganizationRole.Administrator;
        ReceiveOrganizationNotifications = true;
        ReceiveRequestNotifications = true;
        ReceiveMembershipNotifications = true;
    }

    public void DemoteToMember()
    {
        EnsureApproved();
        Role = OrganizationRole.Member;
    }

    public void Revoke(Guid revokedByUserId, DateTimeOffset revokedUtc, string reason)
    {
        EnsureApproved();
        Status = MembershipStatus.Revoked;
        ReceiveOrganizationNotifications = false;
        ReceiveRequestNotifications = false;
        ReceiveMembershipNotifications = false;
        EndedByUserId = revokedByUserId;
        EndedUtc = revokedUtc;
        EndReason = NormalizeReason(reason);
    }

    public void SetNotificationSubscriptions(
        bool receiveOrganizationNotifications,
        bool receiveRequestNotifications,
        bool receiveMembershipNotifications)
    {
        EnsureApproved();
        if (Role != OrganizationRole.Administrator)
        {
            throw new InvalidOperationException("Only organization administrators can subscribe to organization-wide notifications.");
        }

        ReceiveOrganizationNotifications = receiveOrganizationNotifications;
        ReceiveRequestNotifications = receiveRequestNotifications;
        ReceiveMembershipNotifications = receiveMembershipNotifications;
    }

    private void EnsurePending()
    {
        if (Status != MembershipStatus.Pending)
        {
            throw new InvalidOperationException("Only pending memberships can be approved or rejected.");
        }
    }

    private void EnsureApproved()
    {
        if (Status != MembershipStatus.Approved)
        {
            throw new InvalidOperationException("The membership must be approved for this operation.");
        }
    }

    private static string NormalizeReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required.", nameof(reason));
        }

        return reason.Trim();
    }
}
