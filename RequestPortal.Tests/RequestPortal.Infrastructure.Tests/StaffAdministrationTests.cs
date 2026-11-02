using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StaffAdministrationTests
{
    private static VerifiedIdentity Proof(string subject) => new(new ExternalIdentityKey("staff-oidc", "tenant", subject), AuthenticationProviderKind.EntraId, subject, subject, DateTimeOffset.UtcNow, "validated-oidc");
    private static ClaimsPrincipal Actor(SecurityAccount account, bool staff = true) => new(new ClaimsIdentity(staff ?
        [new Claim(ClaimTypes.NameIdentifier, account.Id),new Claim(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue)] :
        [new Claim(ClaimTypes.NameIdentifier, account.Id)], "test"));
    private static (RequestPortalAccounts Accounts, StaffAdministrationService Service, PermissionAuthorizer Authorizer) Setup(PostgreSqlTestDatabase database)
    {
        var accounts = new RequestPortalAccounts(database.Db.Database.GetConnectionString()!); accounts.EnsureSchema();
        var roles = new RequestPortalAuthorizationStore(database.Db,Options.Create(new ActiveDirectoryOptions()));
        var authorizer = new PermissionAuthorizer(roles);
        return (accounts,new StaffAdministrationService(accounts,database.Db,roles,authorizer),authorizer);
    }
    private static Task Bootstrap(StaffAdministrationService service, SecurityAccount account, string subject) => service.BootstrapAsync(account.Id,"staff-oidc","tenant",subject,"approved-operator","verified directory identity");

    [Fact]
    public async Task BootstrapRequiresExactIdentityAndIsPersistentAndIdempotent()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken); var (accounts,service,auth) = Setup(database);
        var admin = accounts.Staff.Resolve(Proof("admin"),AccountPopulation.Staff,true).Account;
        Assert.False(await service.IsProvisionedAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Bootstrap(service,admin,"wrong-subject"));
        await Bootstrap(service,admin,"admin"); await Bootstrap(service,admin,"admin");
        Assert.True(await service.IsProvisionedAsync());
        Assert.Single(accounts.Staff.List());
        foreach (var permission in new[]{RequestPortalPermissions.Access,RequestPortalPermissions.ManageSecurity,RequestPortalPermissions.ViewManagementDashboard,RequestPortalPermissions.ReviewRequests})
            Assert.True((await auth.AuthorizeAsync(AuthorizationSubject.Create(admin.Id),permission,TestContext.Current.CancellationToken)).Allowed);
        Assert.Equal(SecurityAccountStatus.Active,accounts.Staff.Find(admin.Id)!.Status);
        Assert.Single((await service.DirectoryAsync(Actor(admin))).Single().Roles);
        Assert.Contains(await service.HistoryAsync(Actor(admin),admin.Id),x=>x.Action.StartsWith("Bootstrap:"));
        var other = accounts.Staff.Resolve(Proof("other"),AccountPopulation.Staff,true).Account;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Bootstrap(service,other,"other"));
    }

    [Fact]
    public async Task PendingApprovalRolesSuspensionRevocationAndAuditAreEnforced()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken); var (accounts,service,auth) = Setup(database);
        var admin = accounts.Staff.Resolve(Proof("admin"),AccountPopulation.Staff,true).Account; await Bootstrap(service,admin,"admin");
        var pending = accounts.Staff.Resolve(Proof("reviewer"),AccountPopulation.Staff,true).Account;
        Assert.Empty(await service.PermissionsAsync(Actor(admin),pending.Id));
        await service.ChangeAsync(Actor(admin),pending.Id,"Approve","approved reviewer",[RequestPortalRoles.Reviewer],pending.SessionVersion,true);
        Assert.Equal(SecurityAccountStatus.Active,accounts.Staff.Find(pending.Id)!.Status);
        Assert.Contains(RequestPortalPermissions.Access,await service.PermissionsAsync(Actor(admin),pending.Id));
        Assert.False((await auth.AuthorizeAsync(AuthorizationSubject.Create(pending.Id),RequestPortalPermissions.ManageSecurity,TestContext.Current.CancellationToken)).Allowed);
        await service.ChangeAsync(Actor(admin),pending.Id,"Suspend","leave",[RequestPortalRoles.Reviewer],accounts.Staff.Find(pending.Id)!.SessionVersion,true);
        Assert.Empty(await service.PermissionsAsync(Actor(admin),pending.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.DirectoryAsync(Actor(pending)));
        await service.ChangeAsync(Actor(admin),pending.Id,"Activate","return",[RequestPortalRoles.Reviewer],accounts.Staff.Find(pending.Id)!.SessionVersion,true);
        await service.ChangeAsync(Actor(admin),pending.Id,"Revoke","access ended",[],accounts.Staff.Find(pending.Id)!.SessionVersion,true);
        Assert.Empty((await service.DirectoryAsync(Actor(admin))).Single(x=>x.Account.Id==pending.Id).Roles);
        var history = await service.HistoryAsync(Actor(admin),pending.Id);
        foreach(var action in new[]{"Approve:","Suspend:","Activate:","Revoke:"}) Assert.Contains(history,x=>x.Action.StartsWith(action));
    }

    [Theory]
    [InlineData("Suspend")]
    [InlineData("Revoke")]
    [InlineData("Save")]
    public async Task LastAdministratorIsProtected(string action)
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken); var (accounts,service,_) = Setup(database);
        var admin = accounts.Staff.Resolve(Proof("admin"),AccountPopulation.Staff,true).Account; await Bootstrap(service,admin,"admin");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ChangeAsync(Actor(admin),admin.Id,action,"mistake",[RequestPortalRoles.Reviewer],accounts.Staff.Find(admin.Id)!.SessionVersion,true));
        Assert.Equal(SecurityAccountStatus.Active,accounts.Staff.Find(admin.Id)!.Status);
        Assert.Contains(RequestPortalRoles.Administrator,(await service.DirectoryAsync(Actor(admin))).Single().Roles);
    }

    [Fact]
    public async Task UnauthorizedPublicAndPendingAccountsCannotAdministerStaff()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken); var (accounts,service,_) = Setup(database);
        var admin = accounts.Staff.Resolve(Proof("admin"),AccountPopulation.Staff,true).Account; await Bootstrap(service,admin,"admin");
        var pending = accounts.Staff.Resolve(Proof("pending"),AccountPopulation.Staff,true).Account;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.DirectoryAsync(new ClaimsPrincipal()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.DirectoryAsync(Actor(pending)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.DirectoryAsync(Actor(admin,false)));
        var publicProof = new VerifiedIdentity(new ExternalIdentityKey("Google","public","org-manager"),AuthenticationProviderKind.External,"manager@example.test","Organization Manager",DateTimeOffset.UtcNow,"oauth",claims:new Dictionary<string,string>{{"contact_email","manager@example.test"}});
        var manager = accounts.Public.Resolve(publicProof,AccountPopulation.PublicApplicant,true).Account;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.DirectoryAsync(Actor(manager)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Bootstrap(service,manager,"org-manager"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ChangeAsync(Actor(admin),manager.Id,"Approve","wrong population",[RequestPortalRoles.Administrator],manager.SessionVersion,true));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ChangeAsync(Actor(admin),pending.Id,"Approve","no confirmation",[RequestPortalRoles.Reviewer],pending.SessionVersion,false));
        Assert.Equal(SecurityAccountStatus.Active,accounts.Public.Find(manager.Id)!.Status);
    }
    [Fact]
    public async Task BootstrapNeverResetsLaterLifecycleDecisions()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var (accounts,service,_) = Setup(database);
        var first = accounts.Staff.Resolve(Proof("first"),AccountPopulation.Staff,true).Account;
        await Bootstrap(service,first,"first");
        var version = accounts.Staff.Find(first.Id)!.SessionVersion;
        await Bootstrap(service,first,"first");
        Assert.Equal(version,accounts.Staff.Find(first.Id)!.SessionVersion);
        var second = accounts.Staff.Resolve(Proof("second"),AccountPopulation.Staff,true).Account;
        await service.ChangeAsync(Actor(first),second.Id,"Approve","backup administrator",[RequestPortalRoles.Administrator],second.SessionVersion,true);
        await service.ChangeAsync(Actor(second),first.Id,"Suspend","approved leave",[RequestPortalRoles.Administrator],version,true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Bootstrap(service,first,"first"));
        Assert.Equal(SecurityAccountStatus.Suspended,accounts.Staff.Find(first.Id)!.Status);
        Assert.True(await service.IsProvisionedAsync());
    }

    [Fact]
    public async Task SignInAppliesApprovedPermissionsAndSuspensionAndRecordsHistory()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var (accounts,service,authorizer) = Setup(database);
        var admin = accounts.Staff.Resolve(Proof("admin"),AccountPopulation.Staff,true).Account;
        await Bootstrap(service,admin,"admin");
        var signIn = new StaffOidcPrincipalService(accounts,authorizer);
        var pendingResult = await signIn.BuildPrincipalAsync(Proof("reviewer"),TestContext.Current.CancellationToken);
        Assert.False(pendingResult.Principal!.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
        var reviewer = accounts.Staff.List().Single(x=>x.LoginName=="reviewer");
        await service.ChangeAsync(Actor(admin),reviewer.Id,"Approve","approved",[RequestPortalRoles.Reviewer],reviewer.SessionVersion,true,"Updated Reviewer","reviewer@example.test");
        var approved = await signIn.BuildPrincipalAsync(Proof("reviewer"),TestContext.Current.CancellationToken);
        Assert.True(approved.Principal!.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
        Assert.False(approved.Principal.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ManageSecurity));
        var entry = (await service.DirectoryAsync(Actor(admin))).Single(x=>x.Account.Id==reviewer.Id);
        Assert.Equal("Updated Reviewer",entry.Account.DisplayName); Assert.Equal("reviewer@example.test",entry.Email);
        Assert.NotNull(entry.LastSignIn);
        Assert.Contains(await service.HistoryAsync(Actor(admin),reviewer.Id),x=>x.Action=="StaffAuthenticated");
        await service.ChangeAsync(Actor(admin),reviewer.Id,"Suspend","leave",[RequestPortalRoles.Reviewer],accounts.Staff.Find(reviewer.Id)!.SessionVersion,true);
        Assert.False((await signIn.BuildPrincipalAsync(Proof("reviewer"),TestContext.Current.CancellationToken)).Allowed);
    }

    [Fact]
    public async Task StaleChangesRollBackAndRejectionIsAudited()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var (accounts,service,_) = Setup(database);
        var admin = accounts.Staff.Resolve(Proof("admin"),AccountPopulation.Staff,true).Account;
        await Bootstrap(service,admin,"admin");
        var pending = accounts.Staff.Resolve(Proof("pending"),AccountPopulation.Staff,true).Account;
        await service.ChangeAsync(Actor(admin),pending.Id,"Reject","not authorized",[],pending.SessionVersion,true);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ChangeAsync(Actor(admin),pending.Id,"Activate","stale",[RequestPortalRoles.Administrator],pending.SessionVersion,true));
        Assert.Equal(SecurityAccountStatus.Disabled,accounts.Staff.Find(pending.Id)!.Status);
        Assert.Empty((await service.DirectoryAsync(Actor(admin))).Single(x=>x.Account.Id==pending.Id).Roles);
        Assert.Contains(await service.HistoryAsync(Actor(admin),pending.Id),x=>x.Action.StartsWith("Reject:"));
    }

    [Fact]
    public async Task MultipleRoleAssignmentsAreUniqueAndIndividualRevocationIsEffective()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var (accounts,service,_) = Setup(database);
        var admin = accounts.Staff.Resolve(Proof("admin"),AccountPopulation.Staff,true).Account;
        await Bootstrap(service,admin,"admin");
        var staff = accounts.Staff.Resolve(Proof("staff"),AccountPopulation.Staff,true).Account;
        await service.ChangeAsync(Actor(admin),staff.Id,"Approve","both roles",[RequestPortalRoles.Administrator,RequestPortalRoles.Reviewer,RequestPortalRoles.Reviewer],staff.SessionVersion,true);
        Assert.Equal(2,(await service.DirectoryAsync(Actor(admin))).Single(x=>x.Account.Id==staff.Id).Roles.Count);
        await service.ChangeAsync(Actor(admin),staff.Id,"Save","revoke administrator only",[RequestPortalRoles.Reviewer],accounts.Staff.Find(staff.Id)!.SessionVersion,true);
        var permissions = await service.PermissionsAsync(Actor(admin),staff.Id);
        Assert.Contains(RequestPortalPermissions.Access,permissions);
        Assert.DoesNotContain(RequestPortalPermissions.ManageSecurity,permissions);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.DirectoryAsync(Actor(staff)));
    }

    [Fact]
    public async Task ConcurrentAdministratorRemovalsLeaveAnActiveAdministrator()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var (accounts,service,_) = Setup(database);
        var first = accounts.Staff.Resolve(Proof("first"),AccountPopulation.Staff,true).Account;
        await Bootstrap(service,first,"first");
        var second = accounts.Staff.Resolve(Proof("second"),AccountPopulation.Staff,true).Account;
        await service.ChangeAsync(Actor(first),second.Id,"Approve","second administrator",[RequestPortalRoles.Administrator],second.SessionVersion,true);
        var connection = database.Db.Database.GetConnectionString()!;
        async Task<bool> SuspendSelf(string id)
        {
            using var db = new RequestPortal.Infrastructure.Persistence.RequestPortalDbContext(new DbContextOptionsBuilder<RequestPortal.Infrastructure.Persistence.RequestPortalDbContext>().UseNpgsql(connection).Options);
            var stores = new RequestPortalAccounts(connection);
            var roleStore = new RequestPortalAuthorizationStore(db,Options.Create(new ActiveDirectoryOptions()));
            await roleStore.EnsureInitializedAsync(TestContext.Current.CancellationToken);
            var administration = new StaffAdministrationService(stores,db,roleStore,new PermissionAuthorizer(roleStore));
            var account = stores.Staff.Find(id)!;
            try { await administration.ChangeAsync(Actor(account),id,"Suspend","concurrent leave",[RequestPortalRoles.Administrator],account.SessionVersion,true); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(Task.Run(()=>SuspendSelf(first.Id),TestContext.Current.CancellationToken),Task.Run(()=>SuspendSelf(second.Id),TestContext.Current.CancellationToken));
        Assert.Single(outcomes,x=>x);
        Assert.Single(accounts.Staff.List(),x=>x.Status==SecurityAccountStatus.Active);
        Assert.True(await service.HasAdministratorAsync());
    }

}
