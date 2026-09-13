using Common.Diagnostics;
using Microsoft.EntityFrameworkCore;
using RequestPortal.Domain.Organizations;
using RequestPortal.Domain.Requests;
using RequestPortal.Infrastructure.Persistence;
using RequestPortal.Infrastructure.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class PortalAuthorizationIsolationTests
{
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
        Assert.Equal("RequestPortal.Security.Authorization", denied.Source);
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

        AssistanceRequest? result = await context.Service.GetRequestAsync(userA, requestB.Id, isStaff: true, cancellationToken);
        Assert.NotNull(result);
    }

    private static Organization CreateApprovedOrganization(Guid userId, string name)
    {
        Organization organization = new(Guid.NewGuid(), name, userId, DateTimeOffset.UtcNow);
        organization.Approve(userId, DateTimeOffset.UtcNow);
        return organization;
    }

    private static AssistanceRequest CreateRequest(Guid organizationId, Guid userId, string reference)
    {
        return new AssistanceRequest(Guid.NewGuid(), reference, organizationId, userId, DateTimeOffset.UtcNow);
    }

    private sealed class PortalTestContext : IAsyncDisposable
    {
        public RequestPortalDbContext Db { get; }
        public PortalAuthorizationService Service { get; }
        public CapturingDiagnostics Diagnostics { get; }

        private PortalTestContext(RequestPortalDbContext db, PortalAuthorizationService service, CapturingDiagnostics diagnostics)
        {
            Db = db;
            Service = service;
            Diagnostics = diagnostics;
        }

        public static Task<PortalTestContext> CreateAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Preserve existing test fixture implementation.");
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class CapturingDiagnostics
    {
        public List<DiagnosticTelemetryEvent> Events { get; } = [];
    }
}
