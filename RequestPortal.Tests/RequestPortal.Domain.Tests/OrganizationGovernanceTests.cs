using System;
using RequestPortal.Domain.Organizations;
using Xunit;

namespace RequestPortal.Domain.Tests;

public sealed class OrganizationGovernanceTests
{
    [Fact]
    public void CannotDemoteLastApprovedAdministrator()
    {
        Guid userId = Guid.NewGuid();
        Organization organization = new(Guid.NewGuid(), "Test Organization", "JM", userId, DateTimeOffset.UtcNow);
        OrganizationMembership membership = organization.RequestMembership(Guid.NewGuid(), userId, DateTimeOffset.UtcNow);
        organization.Approve(Guid.NewGuid(), DateTimeOffset.UtcNow);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => organization.DemoteAdministrator(membership.Id));
        Assert.Contains("at least one approved administrator", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CannotRevokeLastApprovedAdministrator()
    {
        Guid userId = Guid.NewGuid();
        Organization organization = new(Guid.NewGuid(), "Test Organization", "JM", userId, DateTimeOffset.UtcNow);
        OrganizationMembership membership = organization.RequestMembership(Guid.NewGuid(), userId, DateTimeOffset.UtcNow);
        organization.Approve(Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => organization.RevokeMembership(membership.Id, Guid.NewGuid(), DateTimeOffset.UtcNow, "test"));
    }

    [Fact]
    public void CanDemoteAdministratorWhenAnotherApprovedAdministratorExists()
    {
        Guid firstUserId = Guid.NewGuid();
        Organization organization = new(Guid.NewGuid(), "Test Organization", "JM", firstUserId, DateTimeOffset.UtcNow);
        OrganizationMembership first = organization.RequestMembership(Guid.NewGuid(), firstUserId, DateTimeOffset.UtcNow);
        organization.Approve(Guid.NewGuid(), DateTimeOffset.UtcNow);
        OrganizationMembership second = organization.RequestMembership(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        second.Approve(Guid.NewGuid(), DateTimeOffset.UtcNow);
        organization.PromoteToAdministrator(second.Id);

        organization.DemoteAdministrator(first.Id);
        Assert.Equal(OrganizationRole.Member, first.Role);
    }

    [Fact]
    public void ExpiredRegistrationCannotSubmitNewRequest()
    {
        Guid userId = Guid.NewGuid();
        Organization organization = new(Guid.NewGuid(), "Expired Organization", "JM", userId, DateTimeOffset.UtcNow);
        organization.RequestMembership(Guid.NewGuid(), userId, DateTimeOffset.UtcNow);
        organization.UpdateProfile("REG-1", null, null, null, null, null, new DateOnly(2026, 9, 1));
        organization.Approve(Guid.NewGuid(), DateTimeOffset.UtcNow);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => organization.EnsureEligibleForSubmission(new DateOnly(2026, 9, 12)));

        Assert.Contains("expired", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CurrentApprovedRegistrationCanSubmitNewRequest()
    {
        Guid userId = Guid.NewGuid();
        Organization organization = new(Guid.NewGuid(), "Current Organization", "JM", userId, DateTimeOffset.UtcNow);
        organization.RequestMembership(Guid.NewGuid(), userId, DateTimeOffset.UtcNow);
        organization.UpdateProfile("REG-2", null, null, null, null, null, new DateOnly(2027, 9, 1));
        organization.Approve(Guid.NewGuid(), DateTimeOffset.UtcNow);

        organization.EnsureEligibleForSubmission(new DateOnly(2026, 9, 12));
    }
}
