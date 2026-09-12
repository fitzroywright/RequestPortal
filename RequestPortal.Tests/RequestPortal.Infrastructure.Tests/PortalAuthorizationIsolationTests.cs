using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Common.Diagnostics;
using Microsoft.EntityFrameworkCore;
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
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken);
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
        await context.Db.SaveChangesAsync(cancellationToken);

        IReadOnlyList<RequestPortal.Application.Portal.RequestSummary> results =
            await context.Service.GetRequestsForUserAsync(userId, cancellationToken);

        RequestPortal.Application.Portal.RequestSummary result = Assert.Single(results);
        Assert.Equal("RP-APPROVED", result.PublicReference);
    }

    [Fact]
    public async Task GetRequestsForUserAsync_AllowsMultipleIndependentlyApprovedOrganizations()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken);
        Guid userId = Guid.NewGuid();
        Guid owner2 = Guid.NewGuid();

        Organization first = CreateApprovedOrganization(userId, "First Org");
        Organization second = CreateApprovedOrganization(owner2, "Second Org");
        AddApprovedMember(second, userId, approve: true);

        context.Db.Organizations.AddRange(first, second);
        context.Db.AssistanceRequests.AddRange(
            CreateRequest(first.Id, userId, "RP-FIRST"),
            CreateRequest(second.Id, userId, "RP-SECOND"));
        await context.Db.SaveChangesAsync(cancellationToken);

        IReadOnlyList<RequestPortal.Application.Portal.RequestSummary> results =
            await context.Service.GetRequestsForUserAsync(userId, cancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, item => item.PublicReference == "RP-FIRST");
        Assert.Contains(results, item => item.PublicReference == "RP-SECOND");
    }

    [Fact]
    public async Task GetRequestAsync_DeniesCrossOrganizationRepresentativeAndEmitsDiagnostic()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken);
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        Organization organizationA = CreateApprovedOrganization(userA, "Org A");
        Organization organizationB = CreateApprovedOrganization(userB, "Org B");
        AssistanceRequest requestB = CreateRequest(organizationB.Id, userB, "RP-B");

        context.Db.Organizations.AddRange(organizationA, organizationB);
        context.Db.AssistanceRequests.Add(requestB);
        await context.Db.SaveChangesAsync(cancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.GetRequestAsync(userA, requestB.Id, isStaff: false, cancellationToken));

        DiagnosticTelemetryEvent denied = Assert.Single(
            context.Diagnostics.Events,
            item => item.Code == "RP-AUTH-ORG-DENIED");
        Assert.Equal("Security.Authorization", denied.Source);
        Assert.DoesNotContain("RP-B", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRequestAsync_AllowsStaffCrossOrganizationRead()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken);
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        Organization organizationA = CreateApprovedOrganization(userA, "Org A");
        Organization organizationB = CreateApprovedOrganization(userB, "Org B");
        AssistanceRequest requestB = CreateRequest(organizationB.Id, userB, "RP-B");

        context.Db.Organizations.AddRange(organizationA, organizationB);
        context.Db.AssistanceRequests.Add(requestB);
        await context.Db.SaveChangesAsync(cancellationToken);

        RequestPortal.Application.Portal.RequestDetail? result =
            await context.Service.GetRequestAsync(userA, requestB.Id, isStaff: true, cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(requestB.Id, result.Id);
        Assert.Equal(organizationB.Id, result.OrganizationId);
    }

    [Fact]
    public async Task CreateRequestAsync_DeniesCrossOrganizationWriteAndCreatesNothing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken);
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        Organization organizationA = CreateApprovedOrganization(userA, "Org A");
        Organization organizationB = CreateApprovedOrganization(userB, "Org B");
        context.Db.Organizations.AddRange(organizationA, organizationB);
        await context.Db.SaveChangesAsync(cancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.CreateRequestAsync(userA, organizationB.Id, "Attempted cross-org request", cancellationToken));

        Assert.Empty(await context.Db.AssistanceRequests.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task AddRequestItemAsync_DeniesCrossOrganizationMutation()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken);
        Guid userA = Guid.NewGuid();
        Guid userB = Guid.NewGuid();

        Organization organizationA = CreateApprovedOrganization(userA, "Org A");
        Organization organizationB = CreateApprovedOrganization(userB, "Org B");
        AssistanceRequest requestB = CreateRequest(organizationB.Id, userB, "RP-B");

        context.Db.Organizations.AddRange(organizationA, organizationB);
        context.Db.AssistanceRequests.Add(requestB);
        await context.Db.SaveChangesAsync(cancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.AddRequestItemAsync(userA, requestB.Id, "Unauthorized item", 1m, Guid.NewGuid(), cancellationToken));

        Assert.Empty(await context.Db.AssistanceRequestItems
            .Where(item => item.RequestId == requestB.Id)
            .ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task OrganizationAdministratorRights_DoNotCrossOrganizationBoundary()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken);
        Guid adminA = Guid.NewGuid();
        Guid adminB = Guid.NewGuid();
        Guid pendingUser = Guid.NewGuid();

        Organization organizationA = CreateApprovedOrganization(adminA, "Org A");
        Organization organizationB = CreateApprovedOrganization(adminB, "Org B");
        OrganizationMembership pendingMembershipB = organizationB.RequestMembership(Guid.NewGuid(), pendingUser, Now.AddMinutes(1));

        context.Db.Organizations.AddRange(organizationA, organizationB);
        await context.Db.SaveChangesAsync(cancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.GetPendingMembershipsAsync(adminA, organizationB.Id, cancellationToken));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.ApproveMembershipAsync(adminA, organizationB.Id, pendingMembershipB.Id, cancellationToken));

        OrganizationMembership persisted = await context.Db.OrganizationMemberships
            .SingleAsync(item => item.Id == pendingMembershipB.Id, cancellationToken);
        Assert.Equal(MembershipStatus.Pending, persisted.Status);
    }

    [Fact]
    public async Task OrdinaryMember_CannotExerciseAdministratorRightsInOwnOrganization()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken);
        Guid owner = Guid.NewGuid();
        Guid member = Guid.NewGuid();
        Guid pendingUser = Guid.NewGuid();

        Organization organization = CreateApprovedOrganization(owner, "Org A");
        AddApprovedMember(organization, member, approve: true);
        OrganizationMembership pending = organization.RequestMembership(Guid.NewGuid(), pendingUser, Now.AddMinutes(2));

        context.Db.Organizations.Add(organization);
        await context.Db.SaveChangesAsync(cancellationToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.GetPendingMembershipsAsync(member, organization.Id, cancellationToken));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.ApproveMembershipAsync(member, organization.Id, pending.Id, cancellationToken));
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

    private sealed class PortalTestContext : IAsyncDisposable
    {
        private PortalTestContext(RequestPortalDbContext db, CapturingDiagnosticRouter diagnostics)
        {
            Db = db;
            Diagnostics = diagnostics;
            Service = new PortalWorkflowService(db, new FixedClock(Now), diagnostics);
        }

        public RequestPortalDbContext Db { get; }
        public CapturingDiagnosticRouter Diagnostics { get; }
        public PortalWorkflowService Service { get; }

        public static async Task<PortalTestContext> CreateAsync(CancellationToken cancellationToken)
        {
            DbContextOptions<RequestPortalDbContext> options = new DbContextOptionsBuilder<RequestPortalDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;

            RequestPortalDbContext db = new(options);
            await db.Database.EnsureCreatedAsync(cancellationToken);
            return new PortalTestContext(db, new CapturingDiagnosticRouter());
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

    private sealed class CapturingDiagnosticRouter : IDiagnosticOperationsRouter
    {
        public List<DiagnosticTelemetryEvent> Events { get; } = new();

        public Task RouteAsync(DiagnosticTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(telemetryEvent);
            return Task.CompletedTask;
        }
    }
}
