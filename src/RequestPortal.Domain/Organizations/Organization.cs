using System;
using System.Collections.Generic;

namespace RequestPortal.Domain.Organizations;

public sealed class Organization
{
    private readonly List<OrganizationMembership> memberships = new();

    private Organization()
    {
    }

    public Organization(Guid id, string name, string countryCode, Guid registeredByUserId, DateTimeOffset createdUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Organization id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Organization name is required.", nameof(name));
        }

        Id = id;
        Name = name.Trim();
        CountryCode = countryCode.Trim().ToUpperInvariant();
        RegisteredByUserId = registeredByUserId;
        Status = OrganizationStatus.PendingApproval;
        CreatedUtc = createdUtc;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string CountryCode { get; private set; } = string.Empty;

    public string? RegistrationNumber { get; private set; }

    public OrganizationStatus Status { get; private set; }

    public Guid RegisteredByUserId { get; private set; }

    public Guid? ApprovedByUserId { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset? ApprovedUtc { get; private set; }

    public IReadOnlyCollection<OrganizationMembership> Memberships => memberships.AsReadOnly();

    public void Approve(Guid approvedByUserId, DateTimeOffset approvedUtc)
    {
        if (Status != OrganizationStatus.PendingApproval)
        {
            throw new InvalidOperationException("Only pending organizations can be approved.");
        }

        Status = OrganizationStatus.Approved;
        ApprovedByUserId = approvedByUserId;
        ApprovedUtc = approvedUtc;

        OrganizationMembership? initialMembership = memberships.Find(
            membership => membership.UserId == RegisteredByUserId && membership.Status == MembershipStatus.Pending);

        if (initialMembership is not null)
        {
            initialMembership.ApproveAsAdministrator(approvedByUserId, approvedUtc);
        }
    }

    public OrganizationMembership RequestMembership(Guid membershipId, Guid userId, DateTimeOffset requestedUtc)
    {
        if (memberships.Exists(membership => membership.UserId == userId && membership.Status != MembershipStatus.Revoked))
        {
            throw new InvalidOperationException("The user already has a membership record for this organization.");
        }

        OrganizationMembership membership = new(membershipId, Id, userId, requestedUtc);
        memberships.Add(membership);
        return membership;
    }
}
