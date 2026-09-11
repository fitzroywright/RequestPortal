namespace RequestPortal.Domain.Organizations;

public enum OrganizationStatus
{
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2,
    Suspended = 3
}

public enum MembershipStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Revoked = 3
}

public enum OrganizationRole
{
    Member = 0,
    Administrator = 1,
    Viewer = 2,
    RequestCreator = 3,
    RequestApprover = 4
}
