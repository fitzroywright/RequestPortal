using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Common.Diagnostics;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Portal;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class PortalAuthorizationIsolationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetRequestsForUserAsync_ReturnsOnlyApprovedMembershipOrganizations()
    {
        await using TestContext context = await TestContext.CreateAsync();
        Guid userId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();

        Organization approvedOrganization = CreateApprovedOrganization(userId, "Approved Org");
        Organization pendingOrganization = CreateApprovedOrganization(otherUserId, "Pending Org");
        Organization revokedOrganization = CreateApprovedOrganization(otherUserId, "Revoked Org");
        Organization foreignOrganization = CreateApprovedOrganization(otherUserId, "Foreign Org");

        AddApprovedMember(pendingOrganization, userId, approve: false);
        OrganizationMembership revoked = AddApprovedMember(revokedOrganization, userId, approve: true);
        revoked.Revoke(otherUserId, Now.AddMinutes(2), "Access ended.");

        context.Db.Organizations.AddRange(approvedOrganization, pendingOrganization, revokedOrganization, foreignOrganization);
        context.Db.AssistanceRequests.AddRange(
            CreateRequest(approvedOrganization.Id, userId, "RP-APPROVED"),
            CreateRequest(pendingOrganization.Id, userId, "RP-PENDING"),
            CreateRequest(revokedOrganization.Id, userId, "RP-REVOKED"),
            CreateRequest(foreignOrganization.Id, otherUserId, "RP-FOREIGN"));
        await context.Db.SaveChangesAsync();

        IReadOnlyList<RequestPortal.Application.Portal.RequestSummary> results =
            await context.Service.GetRequestsForUserAsync(userId);

        RequestPortal.Application.Portal.RequestSummary result = Assert.Single(results);
        Assert.Equal("RP-APPROVED", result.PublicReference);
    }

    [Fact]
    public async Task GetRequestsForUserAsync_AllowsMultipleIndependentlyApprovedOrganizations()
    {
        await using TestContext context = await TestContext.CreateAsync();
        Guid userId = Guid.NewGuid();
        Guid owner2 = Guid.NewGuid();

        Organization first = CreateApprovedOrganization(userId, "First Org");
        Organization second = CreateApprovedOrganization(owner2, "Second Org");
        AddApprovedMember(second, userId, approve: true);

        context.Db.Organizations.AddRange(first, second);
        context.Db.AssistanceRequests.AddRange(
            CreateRequest(first.Id, userId, "RP-FIRST"),
            CreateRequest(second.Id, userId, "RP-SECOND"));
        await context.Db.SaveChangesAsync();

        IReadOnlyList<RequestPortal.Application.Portal.RequestSummary> results =
            await context.Service.GetRequestsForUserAsync(userId);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, item => item.PublicReference == "RP-FIRST");
        Assert.Contains(results, item => item.PublicReference == "RP-SECOND");
    }

    [Fact]
    public async Task GetRequestAsync_DeniesCrossOrganizationRepresentativeAndEmitsDiagnostic()
    {
        await using TestContext context = await TestContext.CreateAsync();
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        Organization organizationA = CreateApprovedOrganization(userA, "Org A");
        Organization organizationB = CreateApprovedOrganization(userB, "Org B");
        AssistanceRequest requestB = CreateRequest(organizationB.Id, userB, "RP-B");

        context.Db.Organizations.AddRange(organizationA, organizationB);
        context.Db.AssistanceRequests.Add(requestB);
        await context.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.GetRequestAsync(userA, requestB.Id, isStaff: false));

        StarfleetDiagnosticEvent denied = Assert.Single(
            context.Diagnostics.Events.Where(item => item.Code == "SF-AUTH-ORG-DENIED"));
        Assert.Equal("Security.Authorization", denied.Component);
        Assert.DoesNotContain("RP-B", denied.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("RP-B", denied.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRequestAsync_AllowsStaffCrossOrganizationRead()
    {
        await using TestContext context = await TestContext.CreateAsync();
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        Organization organizationA = CreateApprovedOrganization(userA, "Org A");
        Organization organizationB = CreateApprovedOrganization(userB, "Org B");
        AssistanceRequest requestB = CreateRequest(organizationB.Id, userB, "RP-B");

        context.Db.Organizations.AddRange(organizationA, organizationB);
        context.Db.AssistanceRequests.Add(requestB);
        await context.Db.SaveChangesAsync();

        RequestPortal.Application.Portal.RequestDetail? result =
            await context.Service.GetRequestAsync(userA, requestB.Id, isStaff: true);

        Assert.NotNull(result);
        Assert.Equal(requestB.Id, result.Id);
        Assert.Equal(organizationB.Id, result.OrganizationId);
    }

    [Fact]
    public async Task CreateRequestAsync_DeniesCrossOrganizationWriteAndCreatesNothing()
    {
        await using TestContext context = await TestContext.CreateAsync();
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        Organization organizationA = CreateApprovedOrganization(userA, "Org A");
        Organization organizationB = CreateApprovedOrganization(userB, "Org B");
        context.Db.Organizations.AddRange(organizationA, organizationB);
        await context.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.CreateRequestAsync(userA, organizationB.Id, "Attempted cross-org request"));

        Assert.Empty(await context.Db.AssistanceRequests.ToListAsync());
    }

    [Fact]
    public async Task AddRequestItemAsync_DeniesCrossOrganizationMutation()
    {
        await using TestContext context = await TestContext.CreateAsync();
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        Organization organizationA = CreateApprovedOrganization(userA, "Org A");
        Organization organizationB = CreateApprovedOrganization(userB, "Org B");
        AssistanceRequest requestB = CreateRequest(organizationB.Id, userB, "RP-B");

        context.Db.Organizations.AddRange(organizationA, organizationB);
        context.Db.AssistanceRequests.Add(requestB);
        await context.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.AddRequestItemAsync(userA, requestB.Id, "Unauthorized item", 1m, Guid.NewGuid()));

        Assert.Empty(await context.Db.AssistanceRequestItems.Where(item => item.RequestId == requestB.Id).ToListAsync());
    }

    [Fact]
    public async Task OrganizationAdministratorRights_DoNotCrossOrganizationBoundary()
    {
        await using TestContext context = await TestContext.CreateAsync();
        Guid adminA = Guid.NewGuid();
        Guid adminB = Guid.NewGuid();
        Guid pendingUser = Guid.NewGuid();

        Organization organizationA = CreateApprovedOrganization(adminA, "Org A");
        Organization organizationB = CreateApprovedOrganization(adminB, "Org B");
        OrganizationMembership pendingMembershipB = organizationB.RequestMembership(Guid.NewGuid(), pendingUser, Now.AddMinutes(1));

        context.Db.Organizations.AddRange(organizationA, organizationB);
        await context.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.GetPendingMembershipsAsync(adminA, organizationB.Id));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.ApproveMembershipAsync(adminA, organizationB.Id, pendingMembershipB.Id));

        OrganizationMembership persisted = await context.Db.OrganizationMemberships.SingleAsync(item => item.Id == pendingMembershipB.Id);
        Assert.Equal(MembershipStatus.Pending, persisted.Status);
    }

    [Fact]
    public async Task OrdinaryMember_CannotExerciseAdministratorRightsInOwnOrganization()
    {
        await using TestContext context = await TestContext.CreateAsync();
        Guid owner = Guid.NewGuid();
        Guid member = Guid.NewGuid();
        Guid pendingUser = Guid.NewGuid();

        Organization organization = CreateApprovedOrganization(owner, "Org A");
        AddApprovedMember(organization, member, approve: true);
        OrganizationMembership pending = organization.RequestMembership(Guid.NewGuid(), pendingUser, Now.AddMinutes(2));

        context.Db.Organizations.Add(organization);
        await context.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.GetPendingMembershipsAsync(member, organization.Id));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.ApproveMembershipAsync(member, organization.Id, pending.Id));
    }

    private static Organization CreateApprovedOrganization(Guid ownerUserId, string name)
    {
        Organization organization = new(Guid.NewGuid(), name, "JM", ownerUserId, Now);
        organization.RequestMembership(Guid.NewGuid(), ownerUserId, Now);
        organization.Approve(Guid.NewGuid(), Now.AddMinutes(1));
        return organization;
    }

    private static OrganizationMembership AddApprovedMember(Organization organization, Guid userId, bool approve)
    {
        OrganizationMembership membership = organization.RequestMembership(Guid.NewGuid(), userId, Now.AddMinutes(1));
        if (approve)
        {
            membership.Approve(Guid.NewGuid(), Now.AddMinutes(2));
        }

        return membership;
    }

    private static AssistanceRequest CreateRequest(Guid organizationId, Guid userId, string reference)
    {
        AssistanceRequest request = new(Guid.NewGuid(), reference, organizationId, userId, Now);
        request.SetSummary(reference);
        return request;
    }

    private sealed class TestContext : IAsyncDisposable
    {
        private TestContext(RequestPortalDbContext db, CapturingDiagnosticSink diagnostics)
        {
            Db = db;
            Diagnostics = diagnostics;
            Service = new PortalWorkflowService(db, new FixedClock(Now), diagnostics);
        }

        public RequestPortalDbContext Db { get; }
        public CapturingDiagnosticSink Diagnostics { get; }
        public PortalWorkflowService Service { get; }

        public static async Task<TestContext> CreateAsync()
        {
            DbContextOptions<RequestPortalDbContext> options = new DbContextOptionsBuilder<RequestPortalDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;

            RequestPortalDbContext db = new(options);
            await db.Database.EnsureCreatedAsync();
            return new TestContext(db, new CapturingDiagnosticSink());
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }

    private sealed class CapturingDiagnosticSink : IStarfleetDiagnosticSink
    {
        public List<StarfleetDiagnosticEvent> Events { get; } = new();

        public Task WriteAsync(StarfleetDiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(diagnosticEvent);
            return Task.CompletedTask;
        }
    }
}
