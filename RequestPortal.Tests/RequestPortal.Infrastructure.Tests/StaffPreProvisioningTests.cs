using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StaffPreProvisioningTests
{
    private const string Scope="https://identity.example/realms/staff";
    private static VerifiedIdentity Proof(string id,string? login=null,string? email=null) => new(new("staff",Scope,id),AuthenticationProviderKind.External,login??id,id,DateTimeOffset.UtcNow,"validated-oidc",email);
    private static ClaimsPrincipal Actor(SecurityAccount account,bool staff=true) => new(new ClaimsIdentity(staff
        ? [new Claim(ClaimTypes.NameIdentifier,account.Id),new Claim(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue)]
        : [new Claim(ClaimTypes.NameIdentifier,account.Id)],"test"));
    private sealed class Clock : TimeProvider { public DateTimeOffset Now=DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>Now; }
    private sealed class Directory : IStaffDirectoryLookup
    {
        public DirectoryStaffIdentity? Identity=new("directory-user","new.user","New User","new.user@example.org",true,new("staff",Scope,"directory-user"),AuthenticationProviderKind.External);
        public string? UnavailableReason {get;set;}
        public string ConfigurationKey {get;set;}="configuration-1";
        public int Searches,Reads;
        public Func<Task>? BeforeRead;
        public Task<IReadOnlyList<DirectoryStaffIdentity>> SearchAsync(string query,CancellationToken token) { Searches++; return Task.FromResult<IReadOnlyList<DirectoryStaffIdentity>>(Identity is null?[]:[Identity]); }
        public async Task<DirectoryStaffIdentity?> FindAsync(string id,CancellationToken token) { Reads++; if(BeforeRead is not null) await BeforeRead(); return Identity; }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public required PostgreSqlTestDatabase Database;
        public required RequestPortalAccounts Accounts;
        public required StaffAdministrationService Administration;
        public required PermissionAuthorizer Authorizer;
        public required SecurityAccount Admin;
        public readonly Directory Directory=new();
        public readonly Clock Clock=new();
        public readonly IDataProtectionProvider Protection=new EphemeralDataProtectionProvider();
        public ClaimsPrincipal Principal => Actor(Admin);
        public StaffPreProvisioningService Service => new(Administration,Accounts,Database.Db,Directory,Protection,Clock);
        public Task<IReadOnlyList<StaffDirectoryChoice>> Search() => Service.SearchAsync(Principal,"new",TestContext.Current.CancellationToken);
        public async Task<string> Selection() => (await Search()).Single().Selection;
        public Task<StaffProvisioningResult> Provision(string selection,string[]? roles=null,bool active=true) => Service.ProvisionAsync(Principal,selection,roles??[PortalRoles.Reviewer],active,TestContext.Current.CancellationToken);
        public ValueTask DisposeAsync()=>Database.DisposeAsync();
    }
    private static async Task<Fixture> Setup()
    {
        var database=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts=new RequestPortalAccounts(database.Db.Database.GetConnectionString()!); accounts.EnsureSchema();
        var roles=new RequestPortalAuthorizationStore(database.Db,Options.Create(new ActiveDirectoryOptions()));
        var auth=new PermissionAuthorizer(roles);
        var administration=new StaffAdministrationService(accounts,database.Db,roles,auth);
        var admin=accounts.Staff.Resolve(Proof("admin"),AccountPopulation.Staff,true).Account;
        await administration.BootstrapAsync(admin.Id,"staff",Scope,"admin","approved-operator","explicit identity verified");
        return new(){Database=database,Accounts=accounts,Administration=administration,Authorizer=auth,Admin=accounts.Staff.Find(admin.Id)!};
    }

    [Theory]
    [InlineData("Reviewer",true)] [InlineData("Administrator",true)] [InlineData("Reviewer",false)]
    public async Task ProvisionedIdentityHasCorrectAccessAndFirstSignInReusesAccount(string role,bool activate)
    {
        await using var f=await Setup();
        var result=await f.Provision(await f.Selection(),[role,role],activate);
        Assert.True(result.Created);
        var account=f.Accounts.Staff.Find(result.AccountId)!;
        Assert.Equal(activate?SecurityAccountStatus.Active:SecurityAccountStatus.PendingAccess,account.Status);
        var entry=(await f.Administration.DirectoryAsync(f.Principal)).Single(x=>x.Account.Id==account.Id);
        Assert.Null(entry.LastSignIn);
        Assert.Equal("new.user@example.org",entry.Email);
        Assert.Equal([role],entry.Roles);
        var permissions=await f.Administration.PermissionsAsync(f.Principal,account.Id);
        Assert.Equal(activate&&role=="Administrator",permissions.Contains(RequestPortalPermissions.ManageSecurity));
        Assert.Equal(activate,permissions.Contains(RequestPortalPermissions.ReviewRequests));
        var audit=Assert.Single(await f.Administration.HistoryAsync(f.Principal,account.Id),x=>x.Action=="PreProvisioned");
        Assert.Equal("admin",audit.Actor);
        Assert.Empty(audit.PreviousRoles!); Assert.Equal([role],audit.NewRoles!);
        Assert.Contains("directory-user",audit.Detail);
        var signIn=await new StaffOidcPrincipalService(f.Accounts,f.Authorizer).BuildPrincipalAsync(Proof("directory-user","new.user"),TestContext.Current.CancellationToken);
        Assert.True(signIn.Allowed);
        Assert.Equal(account.Id,signIn.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(2,f.Accounts.Staff.List().Count);
        Assert.NotNull((await f.Administration.DirectoryAsync(f.Principal)).Single(x=>x.Account.Id==account.Id).LastSignIn);
        Assert.Equal(activate,signIn.Principal!.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
        if (!activate) {
            await f.Administration.ChangeAsync(f.Principal,account.Id,"Activate",[role],account.SessionVersion);
            Assert.Equal(SecurityAccountStatus.Active,f.Accounts.Staff.Find(account.Id)!.Status);
        }
    }

    [Fact]
    public async Task RepeatSubmissionDoesNotReactivateOrChangeExistingRoles()
    {
        await using var f=await Setup(); var selection=await f.Selection();
        var first=await f.Provision(selection);
        await f.Administration.ChangeAsync(f.Principal,first.AccountId,"Suspend",[PortalRoles.Reviewer],f.Accounts.Staff.Find(first.AccountId)!.SessionVersion,true);
        var second=await f.Provision(selection,[PortalRoles.Administrator]);
        Assert.False(second.Created); Assert.Equal(first.AccountId,second.AccountId);
        Assert.Equal(SecurityAccountStatus.Suspended,f.Accounts.Staff.Find(first.AccountId)!.Status);
        Assert.Equal([PortalRoles.Reviewer],(await f.Administration.DirectoryAsync(f.Principal)).Single(x=>x.Account.Id==first.AccountId).Roles);
        Assert.Single(await f.Administration.HistoryAsync(f.Principal,first.AccountId),x=>x.Action=="PreProvisioned");
    }

    [Theory]
    [InlineData("tampered")] [InlineData("expired")] [InlineData("configuration")] [InlineData("version")] [InlineData("other-admin")]
    public async Task InvalidSelectionsCannotCreateAccounts(string mode)
    {
        await using var f=await Setup(); var selection=await f.Selection(); var principal=f.Principal;
        switch(mode) {
            case "tampered": selection="invalid"+selection; break;
            case "expired": f.Clock.Now=f.Clock.Now.AddMinutes(11); break;
            case "configuration": f.Directory.ConfigurationKey="new-provider"; break;
            case "version": f.Accounts.Staff.RevokeSessions(f.Admin.Id,f.Admin.Id); break;
            case "other-admin":
                var other=f.Accounts.Staff.Resolve(Proof("other-admin"),AccountPopulation.Staff,true).Account;
                await f.Administration.ChangeAsync(f.Principal,other.Id,"Activate",[PortalRoles.Administrator],other.SessionVersion);
                principal=Actor(other); break;
        }
        int count=f.Accounts.Staff.List().Count;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Service.ProvisionAsync(principal,selection,[PortalRoles.Reviewer],true,TestContext.Current.CancellationToken));
        Assert.Equal(count,f.Accounts.Staff.List().Count);
    }

    [Theory]
    [InlineData("disabled")] [InlineData("deleted")] [InlineData("changed")] [InlineData("unconfigured")]
    public async Task IdentityIsReverifiedBeforeProvisioning(string mode)
    {
        await using var f=await Setup(); var selection=await f.Selection();
        switch(mode) {
            case "disabled": f.Directory.Identity=f.Directory.Identity! with { Enabled=false }; break;
            case "deleted": f.Directory.Identity=null; break;
            case "changed": f.Directory.Identity=f.Directory.Identity! with { Key=new("staff",Scope,"someone-else") }; break;
            case "unconfigured": f.Directory.UnavailableReason="Directory unavailable"; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Provision(selection));
        Assert.Single(f.Accounts.Staff.List());
    }

    [Fact]
    public async Task ReviewerAnonymousAndPublicManagersCannotSearchOrProvision()
    {
        await using var f=await Setup(); var selection=await f.Selection();
        var reviewer=f.Accounts.Staff.Resolve(Proof("reviewer"),AccountPopulation.Staff,true).Account;
        await f.Administration.ChangeAsync(f.Principal,reviewer.Id,"Activate",[PortalRoles.Reviewer],reviewer.SessionVersion);
        var applicant=f.Accounts.Public.Resolve(Proof("public",email:"public@example.org"),AccountPopulation.PublicApplicant,true).Account;
        foreach(var principal in new[]{new ClaimsPrincipal(),Actor(reviewer),Actor(applicant,false),Actor(applicant)}) {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.SearchAsync(principal,"new",TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Service.ProvisionAsync(principal,selection,[PortalRoles.Administrator],true,TestContext.Current.CancellationToken));
        }
        Assert.Equal(1,f.Directory.Searches); Assert.Equal(0,f.Directory.Reads);
    }

    [Fact]
    public async Task ActorRevokedDuringLookupCannotProvision()
    {
        await using var f=await Setup(); var selection=await f.Selection();
        f.Directory.BeforeRead=()=> { f.Accounts.Staff.SetStatus(f.Admin.Id,SecurityAccountStatus.Suspended,"test"); return Task.CompletedTask; };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Provision(selection));
        Assert.Single(f.Accounts.Staff.List());
    }

    [Theory]
    [InlineData("")] [InlineData("FfpAdministrator")] [InlineData("OrganizationManager")] [InlineData("Unknown")]
    public async Task OnlyCanonicalStaffRolesCanBeAssigned(string role)
    {
        await using var f=await Setup();
        await Assert.ThrowsAsync<InvalidOperationException>(async ()=>await f.Provision(await f.Selection(),role.Length==0?[]:[role]));
        Assert.Single(f.Accounts.Staff.List());
    }

    [Fact]
    public async Task ConflictingUsernameDoesNotLinkByNameOrEmail()
    {
        await using var f=await Setup();
        var existing=f.Accounts.Staff.Resolve(Proof("other-subject","new.user"),AccountPopulation.Staff,true).Account;
        await Assert.ThrowsAsync<InvalidOperationException>(async ()=>await f.Provision(await f.Selection()));
        Assert.Equal(2,f.Accounts.Staff.List().Count);
        Assert.Equal("other-subject",Assert.Single(f.Accounts.Staff.GetIdentities(existing.Id)).Key.SubjectId);
    }

    [Fact]
    public async Task AuditFailureRollsBackIdentityProfileAndRoles()
    {
        await using var f=await Setup();
        await f.Database.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_provision_audit() RETURNS trigger AS $$ BEGIN IF NEW.action='PreProvisioned' THEN RAISE EXCEPTION 'test audit failure'; END IF; RETURN NEW; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_provision_audit BEFORE INSERT ON requestportal_staff_admin_events FOR EACH ROW EXECUTE FUNCTION reject_provision_audit();
            """,TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(async ()=>await f.Provision(await f.Selection()));
        Assert.Single(f.Accounts.Staff.List());
        Assert.Equal(1,await f.Database.Db.Users.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1,await f.Database.Db.UserRoles.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RevokedIdentityCannotBeReenrolledThroughPreProvisioning()
    {
        await using var f=await Setup();
        await f.Database.Db.Database.ExecuteSqlRawAsync("INSERT INTO common_security_revoked_identities VALUES('RequestPortal.Staff','staff',{0},'directory-user')",[Scope],TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(async ()=>await f.Provision(await f.Selection()));
        Assert.Single(f.Accounts.Staff.List());
    }
    [Fact]
    public async Task ActorSessionChangeDuringLookupRollsBackCreation()
    {
        await using var f=await Setup(); var selection=await f.Selection();
        f.Directory.BeforeRead=()=> { f.Accounts.Staff.RevokeSessions(f.Admin.Id,"test"); return Task.CompletedTask; };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>f.Provision(selection));
        Assert.Single(f.Accounts.Staff.List());
        Assert.Equal(1,await f.Database.Db.Users.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentDuplicateSubmissionsCreateOneAccountAndOneAudit()
    {
        await using var f=await Setup(); var selection=await f.Selection();
        var connection=f.Database.Db.Database.GetConnectionString()!;
        async Task<StaffProvisioningResult> Submit()
        {
            await using var db=new RequestPortal.Infrastructure.Persistence.RequestPortalDbContext(new DbContextOptionsBuilder<RequestPortal.Infrastructure.Persistence.RequestPortalDbContext>().UseNpgsql(connection).Options);
            var accounts=new RequestPortalAccounts(connection);
            var roles=new RequestPortalAuthorizationStore(db,Options.Create(new ActiveDirectoryOptions()));
            var administration=new StaffAdministrationService(accounts,db,roles,new PermissionAuthorizer(roles));
            return await new StaffPreProvisioningService(administration,accounts,db,f.Directory,f.Protection,f.Clock)
                .ProvisionAsync(f.Principal,selection,[PortalRoles.Reviewer],false,TestContext.Current.CancellationToken);
        }
        var results=await Task.WhenAll(Task.Run(Submit,TestContext.Current.CancellationToken),Task.Run(Submit,TestContext.Current.CancellationToken));
        Assert.Single(results,x=>x.Created);
        Assert.Equal(results[0].AccountId,results[1].AccountId);
        Assert.Equal(2,f.Accounts.Staff.List().Count);
        Assert.Single(await f.Administration.HistoryAsync(f.Principal,results[0].AccountId),x=>x.Action=="PreProvisioned");
    }

}
