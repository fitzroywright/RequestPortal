using System;
using System.Collections.Generic;
using System.Linq;

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
    public string? TaxRegistrationNumber { get; private set; }
    public string? OrganizationType { get; private set; }
    public string? TradingName { get; private set; }
    public DateOnly? DateEstablished { get; private set; }
    public string? CountryOfRegistration { get; private set; }
    public string? Address { get; private set; }
    public string? Parish { get; private set; }
    public string? DistrictArea { get; private set; }
    public string? MailingAddress { get; private set; }
    public string? Phone { get; private set; }
    public string? AlternativePhone { get; private set; }
    public string? Email { get; private set; }
    public string? Website { get; private set; }
    public string? PrimaryContactName { get; private set; }
    public string? PrimaryContactTitle { get; private set; }
    public string? AuthorizedSignatoryName { get; private set; }
    public string? AuthorizedSignatoryTitle { get; private set; }
    public string? SecondaryContactName { get; private set; }
    public string? SecondaryContactEmail { get; private set; }
    public string? SecondaryContactPhone { get; private set; }
    public int? EmployeeCount { get; private set; }
    public int? VolunteerCount { get; private set; }
    public int? ApproximateBeneficiariesServed { get; private set; }
    public string? GeographicServiceArea { get; private set; }
    public string? MainActivities { get; private set; }
    public string? PopulationGroupsServed { get; private set; }
    public bool? PreviouslyReceivedFfpAssistance { get; private set; }
    public decimal? AnnualOperatingBudget { get; private set; }
    public string? PrimaryFundingSources { get; private set; }
    public string? PremisesSituation { get; private set; }
    public DateOnly? RegistrationExpiryDate { get; private set; }

    public string? ApprovalNotes { get; private set; }

    public OrganizationStatus Status { get; private set; }

    public Guid RegisteredByUserId { get; private set; }

    public Guid? ApprovedByUserId { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }

    public DateTimeOffset? ApprovedUtc { get; private set; }

    public IReadOnlyCollection<OrganizationMembership> Memberships => memberships.AsReadOnly();

    public bool IsRegistrationExpired(DateOnly asOfDate)
    {
        return RegistrationExpiryDate.HasValue && RegistrationExpiryDate.Value < asOfDate;
    }

    public void EnsureEligibleForSubmission(DateOnly asOfDate)
    {
        if (Status != OrganizationStatus.Approved)
        {
            throw new InvalidOperationException("Only approved organizations can submit requests.");
        }

        if (IsRegistrationExpired(asOfDate))
        {
            throw new InvalidOperationException("The organization's registration has expired and must be re-verified before new requests can be submitted.");
        }
    }

    public void UpdateProfile(
        string? registrationNumber,
        string? organizationType,
        string? address,
        string? phone,
        string? email,
        string? primaryContactName,
        DateOnly? registrationExpiryDate)
    {
        UpdateProfile(
            registrationNumber,
            null,
            organizationType,
            null,
            null,
            "Jamaica",
            address,
            null,
            null,
            null,
            phone,
            null,
            email,
            null,
            primaryContactName,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            registrationExpiryDate);
    }

    public void UpdateProfile(
        string? registrationNumber,
        string? taxRegistrationNumber,
        string? organizationType,
        string? tradingName,
        DateOnly? dateEstablished,
        string? countryOfRegistration,
        string? address,
        string? parish,
        string? districtArea,
        string? mailingAddress,
        string? phone,
        string? alternativePhone,
        string? email,
        string? website,
        string? primaryContactName,
        string? primaryContactTitle,
        string? authorizedSignatoryName,
        string? authorizedSignatoryTitle,
        string? secondaryContactName,
        string? secondaryContactEmail,
        string? secondaryContactPhone,
        int? employeeCount,
        int? volunteerCount,
        int? approximateBeneficiariesServed,
        string? geographicServiceArea,
        string? mainActivities,
        string? populationGroupsServed,
        bool? previouslyReceivedFfpAssistance,
        decimal? annualOperatingBudget,
        string? primaryFundingSources,
        string? premisesSituation,
        DateOnly? registrationExpiryDate)
    {
        RegistrationNumber = Normalize(registrationNumber);
        TaxRegistrationNumber = Normalize(taxRegistrationNumber);
        OrganizationType = Normalize(organizationType);
        TradingName = Normalize(tradingName);
        DateEstablished = dateEstablished;
        CountryOfRegistration = Normalize(countryOfRegistration) ?? "Jamaica";
        Address = Normalize(address);
        Parish = Normalize(parish);
        DistrictArea = Normalize(districtArea);
        MailingAddress = Normalize(mailingAddress);
        Phone = Normalize(phone);
        AlternativePhone = Normalize(alternativePhone);
        Email = Normalize(email);
        Website = Normalize(website);
        PrimaryContactName = Normalize(primaryContactName);
        PrimaryContactTitle = Normalize(primaryContactTitle);
        AuthorizedSignatoryName = Normalize(authorizedSignatoryName);
        AuthorizedSignatoryTitle = Normalize(authorizedSignatoryTitle);
        SecondaryContactName = Normalize(secondaryContactName);
        SecondaryContactEmail = Normalize(secondaryContactEmail);
        SecondaryContactPhone = Normalize(secondaryContactPhone);
        EmployeeCount = employeeCount;
        VolunteerCount = volunteerCount;
        ApproximateBeneficiariesServed = approximateBeneficiariesServed;
        GeographicServiceArea = Normalize(geographicServiceArea);
        MainActivities = Normalize(mainActivities);
        PopulationGroupsServed = Normalize(populationGroupsServed);
        PreviouslyReceivedFfpAssistance = previouslyReceivedFfpAssistance;
        AnnualOperatingBudget = annualOperatingBudget;
        PrimaryFundingSources = Normalize(primaryFundingSources);
        PremisesSituation = Normalize(premisesSituation);
        RegistrationExpiryDate = registrationExpiryDate;
    }

    public void Approve(Guid approvedByUserId, DateTimeOffset approvedUtc, string? approvalNotes = null)
    {
        if (Status != OrganizationStatus.PendingApproval)
        {
            throw new InvalidOperationException("Only pending organizations can be approved.");
        }

        Status = OrganizationStatus.Approved;
        ApprovedByUserId = approvedByUserId;
        ApprovedUtc = approvedUtc;
        ApprovalNotes = Normalize(approvalNotes);

        OrganizationMembership? initialMembership = memberships.Find(
            membership => membership.UserId == RegisteredByUserId && membership.Status == MembershipStatus.Pending);

        if (initialMembership is not null)
        {
            initialMembership.ApproveAsAdministrator(approvedByUserId, approvedUtc);
        }
    }

    public void Reject(Guid rejectedByUserId, string reason)
    {
        if (Status != OrganizationStatus.PendingApproval)
        {
            throw new InvalidOperationException("Only pending organizations can be rejected.");
        }

        Status = OrganizationStatus.Rejected;
        ApprovedByUserId = rejectedByUserId;
        ApprovalNotes = RequireReason(reason);
    }

    public void Suspend(string? reason)
    {
        if (Status != OrganizationStatus.Approved)
        {
            throw new InvalidOperationException("Only approved organizations can be suspended.");
        }

        Status = OrganizationStatus.Suspended;
        ApprovalNotes = Normalize(reason);
    }

    public void Reactivate(string? notes = null)
    {
        if (Status != OrganizationStatus.Suspended)
        {
            throw new InvalidOperationException("Only suspended organizations can be reactivated.");
        }

        Status = OrganizationStatus.Approved;
        ApprovalNotes = Normalize(notes);
    }

    public OrganizationMembership RequestMembership(Guid membershipId, Guid userId, DateTimeOffset requestedUtc)
    {
        if (memberships.Exists(membership => membership.UserId == userId && membership.Status != MembershipStatus.Revoked && membership.Status != MembershipStatus.Rejected))
        {
            throw new InvalidOperationException("The user already has a membership record for this organization.");
        }

        OrganizationMembership membership = new(membershipId, Id, userId, requestedUtc);
        memberships.Add(membership);
        return membership;
    }

    public void RejectMembership(Guid membershipId, Guid rejectedByUserId, DateTimeOffset rejectedUtc, string reason)
    {
        OrganizationMembership membership = GetMembership(membershipId);
        membership.Reject(rejectedByUserId, rejectedUtc, reason);
    }

    public void PromoteToAdministrator(Guid membershipId)
    {
        OrganizationMembership membership = GetMembership(membershipId);
        membership.PromoteToAdministrator();
    }

    public void DemoteAdministrator(Guid membershipId)
    {
        OrganizationMembership membership = GetMembership(membershipId);
        EnsureAnotherApprovedAdministratorExists(membership.Id);
        membership.DemoteToMember();
    }

    public void RevokeMembership(Guid membershipId, Guid revokedByUserId, DateTimeOffset revokedUtc, string reason)
    {
        OrganizationMembership membership = GetMembership(membershipId);
        if (membership.Role == OrganizationRole.Administrator)
        {
            EnsureAnotherApprovedAdministratorExists(membership.Id);
        }

        membership.Revoke(revokedByUserId, revokedUtc, reason);
    }

    private OrganizationMembership GetMembership(Guid membershipId)
    {
        return memberships.SingleOrDefault(membership => membership.Id == membershipId)
            ?? throw new InvalidOperationException("Organization membership was not found.");
    }

    private void EnsureAnotherApprovedAdministratorExists(Guid excludedMembershipId)
    {
        bool anotherAdministratorExists = memberships.Any(
            membership => membership.Id != excludedMembershipId
                && membership.Status == MembershipStatus.Approved
                && membership.Role == OrganizationRole.Administrator);

        if (!anotherAdministratorExists)
        {
            throw new InvalidOperationException("An organization must retain at least one approved administrator.");
        }
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string RequireReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A reason is required.", nameof(value));
        }

        return value.Trim();
    }
}
