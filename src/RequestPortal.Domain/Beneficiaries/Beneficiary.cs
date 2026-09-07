using System;

namespace RequestPortal.Domain.Beneficiaries;

public sealed class Beneficiary
{
    private Beneficiary()
    {
    }

    public Beneficiary(
        Guid id,
        Guid organizationId,
        string firstName,
        string lastName,
        DateOnly? dateOfBirth,
        string? parentOrGuardian,
        string? location,
        Guid createdByUserId,
        DateTimeOffset createdUtc)
    {
        Id = id;
        OrganizationId = organizationId;
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        DateOfBirth = dateOfBirth;
        ParentOrGuardian = string.IsNullOrWhiteSpace(parentOrGuardian) ? null : parentOrGuardian.Trim();
        Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        CreatedByUserId = createdByUserId;
        CreatedUtc = createdUtc;
    }

    public Guid Id { get; private set; }

    public Guid OrganizationId { get; private set; }

    public string FirstName { get; private set; } = string.Empty;

    public string LastName { get; private set; } = string.Empty;

    public DateOnly? DateOfBirth { get; private set; }

    public string? ParentOrGuardian { get; private set; }

    public string? Location { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }
}
