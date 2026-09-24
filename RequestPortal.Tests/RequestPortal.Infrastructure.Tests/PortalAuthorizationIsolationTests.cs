using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Common.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Application.Auditing;
using RequestPortal.Common.Time;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Organizations;
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
        Guid userId = Guid.NewGuid(); Guid otherUserId = Guid.NewGuid();
        Organization approvedOrganization = CreateApprovedOrganization(userId, "Approved Org"); Organization pendingOrganization = CreateApprovedOrganization(otherUserId, "Pending Org"); Organization revokedOrganization = CreateApprovedOrganization(otherUserId, "Revoked Org"); Organization foreignOrganization = CreateApprovedOrganization(otherUserId, "Foreign Org");
        AddApprovedMember(pendingOrganization, userId, approve: false); OrganizationMembership revoked = AddApprovedMember(revokedOrganization, userId, approve: true); revoked.Revoke(otherUserId, Now.AddMinutes(2), "Access ended.");
        context.Db.Organizations.AddRange(approvedOrganization, pendingOrganization, revokedOrganization, foreignOrganization); context.Db.AssistanceRequests.AddRange(CreateRequest(approvedOrganization.Id, userId, "RP-APPROVED"), CreateRequest(pendingOrganization.Id, userId, "RP-PENDING"), CreateRequest(revokedOrganization.Id, userId, "RP-REVOKED"), CreateRequest(foreignOrganization.Id, otherUserId, "RP-FOREIGN")); await context.Db.SaveChangesAsync(cancellationToken);
        IReadOnlyList<RequestPortal.Application.Portal.RequestSummary> results = await context.Service.GetRequestsForUserAsync(userId, cancellationToken); RequestPortal.Application.Portal.RequestSummary result = Assert.Single(results); Assert.Equal("RP-APPROVED", result.PublicReference);
    }

    [Fact]
    public async Task GetRequestsForUserAsync_AllowsMultipleIndependentlyApprovedOrganizations()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken; await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken); Guid userId = Guid.NewGuid(); Guid owner2 = Guid.NewGuid();
        Organization first = CreateApprovedOrganization(userId, "First Org"); Organization second = CreateApprovedOrganization(owner2, "Second Org"); AddApprovedMember(second, userId, approve: true); context.Db.Organizations.AddRange(first, second); context.Db.AssistanceRequests.AddRange(CreateRequest(first.Id, userId, "RP-FIRST"), CreateRequest(second.Id, userId, "RP-SECOND")); await context.Db.SaveChangesAsync(cancellationToken);
        IReadOnlyList<RequestPortal.Application.Portal.RequestSummary> results = await context.Service.GetRequestsForUserAsync(userId, cancellationToken); Assert.Equal(2, results.Count); Assert.Contains(results, item => item.PublicReference == "RP-FIRST"); Assert.Contains(results, item => item.PublicReference == "RP-SECOND");
    }

    [Fact]
    public async Task GetRequestAsync_DeniesCrossOrganizationRepresentativeAndEmitsDiagnostic()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken; await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken); Guid userA = Guid.NewGuid(); Guid userB = Guid.NewGuid();
        Organization organizationA = CreateApprovedOrganization(userA, "Org A"); Organization organizationB = CreateApprovedOrganization(userB, "Org B"); AssistanceRequest requestB = CreateRequest(organizationB.Id, userB, "RP-B"); context.Db.Organizations.AddRange(organizationA, organizationB); context.Db.AssistanceRequests.Add(requestB); await context.Db.SaveChangesAsync(cancellationToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.Service.GetRequestAsync(userA, requestB.Id, isStaff: false, cancellationToken)); DiagnosticTelemetryEvent denied = Assert.Single(context.Diagnostics.Events, item => item.Code == "RP-AUTH-ORG-DENIED"); Assert.Equal("RequestPortal.Security.Authorization", denied.Source); Assert.DoesNotContain("RP-B", denied.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRequestAsync_AllowsStaffCrossOrganizationRead()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken; await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken); Guid userA = Guid.NewGuid(); Guid userB = Guid.NewGuid(); Organization organizationA = CreateApprovedOrganization(userA, "Org A"); Organization organizationB = CreateApprovedOrganization(userB, "Org B"); AssistanceRequest requestB = CreateRequest(organizationB.Id, userB, "RP-B"); context.Db.Organizations.AddRange(organizationA, organizationB); context.Db.AssistanceRequests.Add(requestB); await context.Db.SaveChangesAsync(cancellationToken); RequestPortal.Application.Portal.RequestDetail? result = await context.Service.GetRequestAsync(userA, requestB.Id, isStaff: true, cancellationToken); Assert.NotNull(result); Assert.Equal(requestB.Id, result.Id); Assert.Equal(organizationB.Id, result.OrganizationId);
    }

    [Fact]
    public async Task CreateRequestAsync_DeniesCrossOrganizationWriteAndCreatesNothing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken; await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken); Guid userA = Guid.NewGuid(); Guid userB = Guid.NewGuid(); Organization organizationA = CreateApprovedOrganization(userA, "Org A"); Organization organizationB = CreateApprovedOrganization(userB, "Org B"); context.Db.Organizations.AddRange(organizationA, organizationB); await context.Db.SaveChangesAsync(cancellationToken); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.Service.CreateRequestAsync(userA, organizationB.Id, "Attempted cross-org request", cancellationToken)); Assert.Empty(await context.Db.AssistanceRequests.ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task AddRequestItemAsync_DeniesCrossOrganizationMutation()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken; await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken); Guid userA = Guid.NewGuid(); Guid userB = Guid.NewGuid(); Organization organizationA = CreateApprovedOrganization(userA, "Org A"); Organization organizationB = CreateApprovedOrganization(userB, "Org B"); AssistanceRequest requestB = CreateRequest(organizationB.Id, userB, "RP-B"); context.Db.Organizations.AddRange(organizationA, organizationB); context.Db.AssistanceRequests.Add(requestB); await context.Db.SaveChangesAsync(cancellationToken); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.Service.AddRequestItemAsync(userA, requestB.Id, "Unauthorized item", 1m, Guid.NewGuid(), cancellationToken)); Assert.Empty(await context.Db.AssistanceRequestItems.Where(item => item.RequestId == requestB.Id).ToListAsync(cancellationToken));
    }

    [Fact]
    public async Task OrganizationAdministratorRights_DoNotCrossOrganizationBoundary()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken; await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken); Guid adminA = Guid.NewGuid(); Guid adminB = Guid.NewGuid(); Guid pendingUser = Guid.NewGuid(); Organization organizationA = CreateApprovedOrganization(adminA, "Org A"); Organization organizationB = CreateApprovedOrganization(adminB, "Org B"); OrganizationMembership pendingMembershipB = organizationB.RequestMembership(Guid.NewGuid(), pendingUser, Now.AddMinutes(1)); context.Db.Organizations.AddRange(organizationA, organizationB); await context.Db.SaveChangesAsync(cancellationToken); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.Service.GetPendingMembershipsAsync(adminA, organizationB.Id, cancellationToken)); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.Service.ApproveMembershipAsync(adminA, organizationB.Id, pendingMembershipB.Id, cancellationToken)); OrganizationMembership persisted = await context.Db.OrganizationMemberships.SingleAsync(item => item.Id == pendingMembershipB.Id, cancellationToken); Assert.Equal(MembershipStatus.Pending, persisted.Status);
    }

    [Fact]
    public async Task OrganizationLifecycle_FirstRegistrantBecomesAdministrator_ThenControlsMembership()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken);

        Guid firstUserId = Guid.NewGuid();
        Guid secondUserId = Guid.NewGuid();
        Guid staffUserId = Guid.NewGuid();

        Guid organizationId = await context.Service.RegisterOrganizationAsync(
            firstUserId,
            "Lifecycle Org",
            "JM",
            cancellationToken);

        Organization pendingOrganization = await context.Db.Organizations
            .Include(item => item.Memberships)
            .SingleAsync(item => item.Id == organizationId, cancellationToken);

        Assert.Equal(OrganizationStatus.PendingApproval, pendingOrganization.Status);
        OrganizationMembership initialMembership = Assert.Single(
            pendingOrganization.Memberships,
            item => item.UserId == firstUserId);
        Assert.Equal(MembershipStatus.Pending, initialMembership.Status);
        Assert.Equal(OrganizationRole.Member, initialMembership.Role);

        await context.Service.ApproveOrganizationAsync(
            staffUserId,
            organizationId,
            cancellationToken);

        // ApproveOrganizationAsync uses a real transaction in production. EF InMemory
        // cannot model transaction/change-tracker semantics reliably across this
        // multi-step lifecycle, so clear tracked instances before continuing with
        // the next independent service operation.
        context.Db.ChangeTracker.Clear();

        OrganizationMembership approvedAdministrator = await context.Db.OrganizationMemberships
            .SingleAsync(
                item => item.OrganizationId == organizationId && item.UserId == firstUserId,
                cancellationToken);

        Assert.Equal(MembershipStatus.Approved, approvedAdministrator.Status);
        Assert.Equal(OrganizationRole.Administrator, approvedAdministrator.Role);
        Assert.Equal(staffUserId, approvedAdministrator.ApprovedByUserId);

        await context.Service.RequestMembershipAsync(
            secondUserId,
            organizationId,
            cancellationToken);

        OrganizationMembership secondMembership = await context.Db.OrganizationMemberships
            .SingleAsync(
                item => item.OrganizationId == organizationId && item.UserId == secondUserId,
                cancellationToken);

        Assert.Equal(MembershipStatus.Pending, secondMembership.Status);
        Assert.Equal(OrganizationRole.Member, secondMembership.Role);

        await context.Service.ApproveMembershipAsync(
            firstUserId,
            organizationId,
            secondMembership.Id,
            cancellationToken);

        secondMembership = await context.Db.OrganizationMemberships
            .SingleAsync(item => item.Id == secondMembership.Id, cancellationToken);

        Assert.Equal(MembershipStatus.Approved, secondMembership.Status);
        Assert.Equal(firstUserId, secondMembership.ApprovedByUserId);

        await context.Governance.RevokeAsync(
            firstUserId,
            organizationId,
            secondMembership.Id,
            "Access removed.",
            cancellationToken);

        secondMembership = await context.Db.OrganizationMemberships
            .SingleAsync(item => item.Id == secondMembership.Id, cancellationToken);

        Assert.Equal(MembershipStatus.Revoked, secondMembership.Status);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => context.Service.CreateRequestAsync(
                secondUserId,
                organizationId,
                "Must not be allowed after revocation",
                cancellationToken));
    }

    [Fact]
    public async Task OrdinaryMember_CannotExerciseAdministratorRightsInOwnOrganization()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken; await using PortalTestContext context = await PortalTestContext.CreateAsync(cancellationToken); Guid owner = Guid.NewGuid(); Guid member = Guid.NewGuid(); Guid pendingUser = Guid.NewGuid(); Organization organization = CreateApprovedOrganization(owner, "Org A"); AddApprovedMember(organization, member, approve: true); OrganizationMembership pending = organization.RequestMembership(Guid.NewGuid(), pendingUser, Now.AddMinutes(2)); context.Db.Organizations.Add(organization); await context.Db.SaveChangesAsync(cancellationToken); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.Service.GetPendingMembershipsAsync(member, organization.Id, cancellationToken)); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.Service.ApproveMembershipAsync(member, organization.Id, pending.Id, cancellationToken));
    }

    private static Organization CreateApprovedOrganization(Guid ownerUserId, string name) { Organization organization = new(Guid.NewGuid(), name, "JM", ownerUserId, Now); organization.RequestMembership(Guid.NewGuid(), ownerUserId, Now); organization.Approve(Guid.NewGuid(), Now.AddMinutes(1)); return organization; }
    private static OrganizationMembership AddApprovedMember(Organization organization, Guid userId, bool approve) { OrganizationMembership membership = organization.RequestMembership(Guid.NewGuid(), userId, Now.AddMinutes(1)); if (approve) membership.Approve(Guid.NewGuid(), Now.AddMinutes(2)); return membership; }
    private static AssistanceRequest CreateRequest(Guid organizationId, Guid userId, string reference) { AssistanceRequest request = new(Guid.NewGuid(), reference, organizationId, userId, Now); request.SetSummary(reference); return request; }

    private sealed class PortalTestContext : IAsyncDisposable
    {
        private PortalTestContext(
            PostgreSqlTestDatabase database,
            CapturingDiagnosticRouter diagnostics)
        {
            databaseHarness = database;
            Db = database.Db;
            Diagnostics = diagnostics;
            FixedClock clock = new(Now);
            Service = new PortalWorkflowService(Db, clock, diagnostics);
            Governance = new OrganizationMembershipGovernanceService(
                Db,
                clock,
                new NullBusinessAuditWriter());
        }

        private readonly PostgreSqlTestDatabase databaseHarness;

        public RequestPortalDbContext Db { get; }
        public CapturingDiagnosticRouter Diagnostics { get; }
        public PortalWorkflowService Service { get; }
        public OrganizationMembershipGovernanceService Governance { get; }

        public static async Task<PortalTestContext> CreateAsync(
            CancellationToken cancellationToken)
        {
            PostgreSqlTestDatabase database =
                await PostgreSqlTestDatabase.CreateAsync(cancellationToken);
            return new PortalTestContext(
                database,
                new CapturingDiagnosticRouter());
        }

        public ValueTask DisposeAsync() => databaseHarness.DisposeAsync();
    }
    private sealed class FixedClock(DateTimeOffset utcNow) : IClock { public DateTimeOffset UtcNow { get; } = utcNow; }

    private sealed class NullBusinessAuditWriter : IBusinessAuditWriter
    {
        public Task WriteAsync(
            Guid actorUserId,
            string action,
            string entityType,
            Guid entityId,
            Guid? organizationId,
            string? detail = null,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingDiagnosticRouter : IDiagnosticOperationsRouter { public List<DiagnosticTelemetryEvent> Events { get; } = new(); public Task RouteAsync(DiagnosticTelemetryEvent telemetryEvent, CancellationToken cancellationToken = default) { Events.Add(telemetryEvent); return Task.CompletedTask; } }
}
