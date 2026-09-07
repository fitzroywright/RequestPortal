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
        Status = MembershipStatus.Approved;
        ApprovedByUserId = approvedByUserId;
        ApprovedUtc = approvedUtc;
    }

    public void Revoke()
    {
        if (Status != MembershipStatus.Approved)
        {
            throw new InvalidOperationException("Only approved memberships can be revoked.");
        }

        Status = MembershipStatus.Revoked;
    }

    private void EnsurePending()
    {
        if (Status != MembershipStatus.Pending)
        {
            throw new InvalidOperationException("Only pending memberships can be approved.");
        }
    }
}
