using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StaffRoleConsolidationTests
{
    [Fact]
    public async Task EquivalentLegacyBindingsConsolidateOnceWithoutChangingPermissionsOrIdentities()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts=Accounts(fixture);var reviewer=Staff(accounts,"reviewer");var administrator=Staff(accounts,"administrator");
        accounts.Staff.SetStatus(reviewer.Id,SecurityAccountStatus.Active,"approved-fixture");
        accounts.Staff.SetStatus(administrator.Id,SecurityAccountStatus.Active,"approved-fixture");
        var oldReviewer=await LegacyRole(fixture,"FfpReviewer",PortalRoles.Reviewer);
        var oldAdmin=await LegacyRole(fixture,"FfpAdministrator",PortalRoles.Administrator);
        await Assign(fixture,reviewer.Id,oldReviewer);await Assign(fixture,administrator.Id,oldAdmin);
        await Sql(fixture,"INSERT INTO requestportal_directory_group_roles(group_name,role_key) VALUES('directory-reviewers','FfpReviewer'),('directory-admins','FfpAdministrator')");
        var store=Store(fixture);var authorizer=new PermissionAuthorizer(store);
        Assert.True((await authorizer.AuthorizeAsync(AuthorizationSubject.Create(reviewer.Id),RequestPortalPermissions.ReviewRequests,TestContext.Current.CancellationToken)).Allowed);
        Assert.False((await authorizer.AuthorizeAsync(AuthorizationSubject.Create(reviewer.Id),RequestPortalPermissions.ManageSecurity,TestContext.Current.CancellationToken)).Allowed);
        var identities=accounts.Staff.GetIdentities(reviewer.Id);long version=accounts.Staff.Find(reviewer.Id)!.SessionVersion;
        await StaffRoleConsolidation.ApplyAsync(fixture.Db,TestContext.Current.CancellationToken);
        long audit=await Count(fixture,"SELECT count(*) FROM requestportal_staff_admin_events");
        await StaffRoleConsolidation.ApplyAsync(fixture.Db,TestContext.Current.CancellationToken);
        Assert.Equal(audit,await Count(fixture,"SELECT count(*) FROM requestportal_staff_admin_events"));
        Assert.Equal(0,await Count(fixture,"SELECT count(*) FROM \"AspNetRoles\" WHERE \"Name\" IN ('FfpAdministrator','FfpReviewer')"));
        Assert.Equal(new[]{PortalRoles.Reviewer},await store.GetDirectRoleKeysAsync(reviewer.Id,TestContext.Current.CancellationToken));
        Assert.Equal(new[]{PortalRoles.Administrator},await store.GetDirectRoleKeysAsync(administrator.Id,TestContext.Current.CancellationToken));
        Assert.Equal(new[]{PortalRoles.Reviewer},await store.GetRoleKeysForGroupsAsync(new[]{"directory-reviewers"},TestContext.Current.CancellationToken));
        Assert.Equal(new[]{PortalRoles.Administrator},await store.GetRoleKeysForGroupsAsync(new[]{"directory-admins"},TestContext.Current.CancellationToken));
        Assert.Equal(version+1,accounts.Staff.Find(reviewer.Id)!.SessionVersion);
        Assert.Equal(identities,accounts.Staff.GetIdentities(reviewer.Id));
        Assert.Equal(SecurityAccountStatus.Active,accounts.Staff.Find(reviewer.Id)!.Status);
        Assert.False((await authorizer.AuthorizeAsync(AuthorizationSubject.Create(reviewer.Id),RequestPortalPermissions.ManageSecurity,TestContext.Current.CancellationToken)).Allowed);
        var signIn=new StaffOidcPrincipalService(accounts,authorizer);
        var reviewPrincipal=await signIn.BuildPrincipalAsync(Proof("reviewer"),TestContext.Current.CancellationToken);
        Assert.True(reviewPrincipal.Principal!.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
        Assert.False(reviewPrincipal.Principal.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ManageSecurity));
        var adminPrincipal=await signIn.BuildPrincipalAsync(Proof("administrator"),TestContext.Current.CancellationToken);
        Assert.True(adminPrincipal.Principal!.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ManageSecurity));
        var service=new StaffAdministrationService(accounts,fixture.Db,store,authorizer);
        Assert.Equal(new[]{PortalRoles.Administrator,PortalRoles.Reviewer},(await service.RolesAsync(adminPrincipal.Principal)).Select(x=>x.Key).Order());
        Assert.Contains(await service.HistoryAsync(adminPrincipal.Principal,reviewer.Id),x=>x.Action=="RolesConsolidated"&&x.PreviousRoles!.Contains("FfpReviewer")&&x.NewRoles!.Contains(PortalRoles.Reviewer));
    }

    [Fact]
    public async Task DuplicateAssignmentsCollapseButUnrelatedRoleMappingsArePreserved()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts=Accounts(fixture);var user=Staff(accounts,"pending");
        var legacy=await LegacyRole(fixture,"FfpReviewer",PortalRoles.Reviewer);
        await Assign(fixture,user.Id,legacy);
        Guid unrelated=Guid.NewGuid();
        await Sql(fixture,"INSERT INTO \"AspNetRoles\"(\"Id\",\"Name\",\"NormalizedName\") VALUES(@id,'OrganizationManager','ORGANIZATIONMANAGER')",("id",unrelated));
        await Assign(fixture,user.Id,unrelated);
        await Sql(fixture,"INSERT INTO \"AspNetUserRoles\"(\"UserId\",\"RoleId\") SELECT @id,\"Id\" FROM \"AspNetRoles\" WHERE \"Name\"='Reviewer'",("id",Guid.Parse(user.Id)));
        await StaffRoleConsolidation.ApplyAsync(fixture.Db,TestContext.Current.CancellationToken);
        Assert.Equal(2,await Count(fixture,"SELECT count(*) FROM \"AspNetUserRoles\""));
        Assert.Equal(SecurityAccountStatus.PendingAccess,accounts.Staff.Find(user.Id)!.Status);
        Assert.Empty(await fixture.Db.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM \"AspNetRoles\" WHERE \"Name\" LIKE 'Ffp%'").ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmptyLegacyGrantsCannotEscalateAnActiveStaffAccount()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts=Accounts(fixture);var user=Staff(accounts,"active");accounts.Staff.SetStatus(user.Id,SecurityAccountStatus.Active,"fixture");
        var role=await LegacyRole(fixture,"FfpAdministrator",null);await Assign(fixture,user.Id,role);
        var exception=await Assert.ThrowsAsync<PostgresException>(()=>StaffRoleConsolidation.ApplyAsync(fixture.Db,TestContext.Current.CancellationToken));
        Assert.Contains("RP-ROLE-005",exception.Message);
        Assert.Equal(1,await Count(fixture,"SELECT count(*) FROM \"AspNetRoles\" WHERE \"Name\"='FfpAdministrator'"));
        Assert.Equal(1,await Count(fixture,"SELECT count(*) FROM \"AspNetUserRoles\" WHERE \"RoleId\"='"+role+"'"));
        Assert.Equal(0,await Count(fixture,"SELECT count(*) FROM requestportal_staff_admin_events"));
    }

    [Fact]
    public async Task BroaderLegacyReviewerPolicyCannotBroadenCanonicalReviewerPermissions()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts=Accounts(fixture);var user=Staff(accounts,"reviewer");
        var role=await LegacyRole(fixture,"FfpReviewer",PortalRoles.Administrator);await Assign(fixture,user.Id,role);
        var exception=await Assert.ThrowsAsync<PostgresException>(()=>StaffRoleConsolidation.ApplyAsync(fixture.Db,TestContext.Current.CancellationToken));
        Assert.Contains("RP-ROLE-002",exception.Message);
        Assert.Equal(1,await Count(fixture,"SELECT count(*) FROM \"AspNetRoles\" WHERE \"Name\"='FfpReviewer'"));
        Assert.Equal(0,await Count(fixture,"SELECT count(*) FROM requestportal_role_permissions WHERE role_key='Reviewer' AND permission_key IN ('*','security.manage')"));
    }

    [Fact]
    public async Task UnknownDirectoryRolePolicyStopsMigrationAndPreservesTheMapping()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await LegacyRole(fixture,"FfpAdministrator",null);
        await Sql(fixture,"INSERT INTO requestportal_directory_group_roles VALUES('protected-directory-group','FfpAdministrator')");
        var exception=await Assert.ThrowsAsync<PostgresException>(()=>StaffRoleConsolidation.ApplyAsync(fixture.Db,TestContext.Current.CancellationToken));
        Assert.Contains("RP-ROLE-002",exception.Message);
        Assert.Equal(1,await Count(fixture,"SELECT count(*) FROM requestportal_directory_group_roles WHERE role_key='FfpAdministrator'"));
    }

    [Fact]
    public async Task RoutineApprovalNeedsNoCommentOrCheckboxAndAuditIncludesBeforeAndAfter()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts=Accounts(fixture);var admin=Staff(accounts,"admin");var pending=Staff(accounts,"pending");
        var roleStore=Store(fixture);var authorizer=new PermissionAuthorizer(roleStore);var service=new StaffAdministrationService(accounts,fixture.Db,roleStore,authorizer);
        await service.BootstrapAsync(admin.Id,"staff-oidc","tenant","admin","fixture-operator","approved bootstrap");
        await service.ChangeAsync(Actor(admin),pending.Id,"Approve",[PortalRoles.Reviewer],pending.SessionVersion);
        Assert.Equal(SecurityAccountStatus.Active,accounts.Staff.Find(pending.Id)!.Status);
        var history=await service.HistoryAsync(Actor(admin),pending.Id);
        var approval=Assert.Single(history,x=>x.Action=="Approve");
        Assert.Empty(approval.PreviousRoles!);Assert.Equal(new[]{PortalRoles.Reviewer},approval.NewRoles);
        Assert.Equal("admin",approval.Actor);Assert.Equal(pending.Id,approval.Subject);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ChangeAsync(Actor(pending),pending.Id,"Save",[PortalRoles.Administrator],accounts.Staff.Find(pending.Id)!.SessionVersion));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.ChangeAsync(Actor(admin),pending.Id,"Suspend",[PortalRoles.Reviewer],accounts.Staff.Find(pending.Id)!.SessionVersion));
        await service.ChangeAsync(Actor(admin),pending.Id,"Save",[PortalRoles.Reviewer],accounts.Staff.Find(pending.Id)!.SessionVersion);
        Assert.Contains(await service.HistoryAsync(Actor(admin),pending.Id),x=>x.Action=="Save");
    }

    [Fact]
    public async Task DirectoryBackedReviewerKeepsVerifiedGroupAccessAfterMigration()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts=Accounts(fixture);
        var proof=new VerifiedIdentity(new ExternalIdentityKey("staff-oidc","tenant","group-reviewer"),AuthenticationProviderKind.EntraId,"group-reviewer","Directory Reviewer",DateTimeOffset.UtcNow,"verified-provider",groups:["directory-reviewers"]);
        var account=accounts.Staff.Resolve(proof,AccountPopulation.Staff,true).Account;
        accounts.Staff.SetStatus(account.Id,SecurityAccountStatus.Active,"fixture");
        await LegacyRole(fixture,"FfpReviewer",PortalRoles.Reviewer);
        await Sql(fixture,"INSERT INTO requestportal_directory_group_roles VALUES('directory-reviewers','FfpReviewer')");
        await StaffRoleConsolidation.ApplyAsync(fixture.Db,TestContext.Current.CancellationToken);
        var signIn=new StaffOidcPrincipalService(accounts,new PermissionAuthorizer(Store(fixture)));
        var result=await signIn.BuildPrincipalAsync(proof,TestContext.Current.CancellationToken);
        Assert.True(result.Allowed);Assert.Equal(account.Id,result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(result.Principal!.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
        Assert.False(result.Principal!.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.ManageSecurity));
        Assert.True(result.Principal!.HasClaim("directory_group","directory-reviewers"));
    }

    [Fact]
    public async Task CustomSecurityManagerCannotSelfGrantAdministratorAndHiddenMappingsSurviveSave()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts=Accounts(fixture);var manager=Staff(accounts,"limited-manager");
        accounts.Staff.SetStatus(manager.Id,SecurityAccountStatus.Active,"fixture");
        Guid other=Guid.NewGuid();
        await Sql(fixture,"INSERT INTO \"AspNetRoles\"(\"Id\",\"Name\",\"NormalizedName\") VALUES(@id,'ExistingManager','EXISTINGMANAGER')",("id",other));
        await Sql(fixture,"INSERT INTO requestportal_role_permissions VALUES('ExistingManager','security.manage')");
        await Assign(fixture,manager.Id,other);
        var store=Store(fixture);var service=new StaffAdministrationService(accounts,fixture.Db,store,new PermissionAuthorizer(store));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ChangeAsync(Actor(manager),manager.Id,"Save",[PortalRoles.Administrator],accounts.Staff.Find(manager.Id)!.SessionVersion));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.ChangeAsync(Actor(manager),manager.Id,"Save",[PortalRoles.Reviewer],accounts.Staff.Find(manager.Id)!.SessionVersion));
        var admin=Staff(accounts,"security-admin");
        await service.BootstrapAsync(admin.Id,"staff-oidc","tenant","security-admin","fixture","approved fixture");
        await service.ChangeAsync(Actor(admin),manager.Id,"Save",[PortalRoles.Reviewer],accounts.Staff.Find(manager.Id)!.SessionVersion);
        Assert.Contains("ExistingManager",await store.GetDirectRoleKeysAsync(manager.Id,TestContext.Current.CancellationToken));
        Assert.Equal(2,(await service.RolesAsync(Actor(manager))).Count);
    }

    [Fact]
    public async Task LegacyClaimsWithDifferentMeaningStopBeforeRemovingAnyRole()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts=Accounts(fixture);var staff=Staff(accounts,"pending-claims");
        var legacy=await LegacyRole(fixture,"FfpReviewer",PortalRoles.Reviewer);await Assign(fixture,staff.Id,legacy);
        await Sql(fixture,"INSERT INTO \"AspNetRoleClaims\"(\"RoleId\",\"ClaimType\",\"ClaimValue\") VALUES(@id,'custom-scope','protected')",("id",legacy));
        var exception=await Assert.ThrowsAsync<PostgresException>(()=>StaffRoleConsolidation.ApplyAsync(fixture.Db,TestContext.Current.CancellationToken));
        Assert.Contains("RP-ROLE-003",exception.Message);
        Assert.Equal(1,await Count(fixture,"SELECT count(*) FROM \"AspNetRoles\" WHERE \"Name\"='FfpReviewer'"));
    }

    [Fact]
    public async Task MisconfiguredReviewerAdministratorGrantsAreNotSilentlyMigrated()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await Sql(fixture,"INSERT INTO requestportal_role_permissions VALUES('Reviewer','security.manage')");
        var exception=await Assert.ThrowsAsync<PostgresException>(()=>StaffRoleConsolidation.ApplyAsync(fixture.Db,TestContext.Current.CancellationToken));
        Assert.Contains("RP-ROLE-007",exception.Message);
    }

    [Fact]
    public async Task ConsolidatingAPublicUsersLegacyRoleDoesNotCreateStaffAccess()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts=Accounts(fixture);
        var proof=new VerifiedIdentity(new ExternalIdentityKey("Google","public","org-manager"),AuthenticationProviderKind.External,"manager@example.test","Organization Manager",DateTimeOffset.UtcNow,"oauth","manager@example.test");
        var manager=accounts.Public.Resolve(proof,AccountPopulation.PublicApplicant,true).Account;
        var legacy=await LegacyRole(fixture,"FfpReviewer",PortalRoles.Reviewer);await Assign(fixture,manager.Id,legacy);
        await StaffRoleConsolidation.ApplyAsync(fixture.Db,TestContext.Current.CancellationToken);
        Assert.Equal(manager,accounts.Public.Find(manager.Id));Assert.Empty(accounts.Staff.List());
        var store=Store(fixture);var service=new StaffAdministrationService(accounts,fixture.Db,store,new PermissionAuthorizer(store));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>service.DirectoryAsync(Actor(manager)));
    }

    [Fact]
    public async Task AdditionalTargetRoleClaimsCannotExpandLegacyUserClaims()
    {
        await using var fixture=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts=Accounts(fixture);var staff=Staff(accounts,"legacy-claims");
        var legacy=await LegacyRole(fixture,"FfpReviewer",PortalRoles.Reviewer);await Assign(fixture,staff.Id,legacy);
        await Sql(fixture,"INSERT INTO \"AspNetRoleClaims\"(\"RoleId\",\"ClaimType\",\"ClaimValue\") SELECT \"Id\",'custom-scope','additional' FROM \"AspNetRoles\" WHERE \"Name\"='Reviewer'");
        var exception=await Assert.ThrowsAsync<PostgresException>(()=>StaffRoleConsolidation.ApplyAsync(fixture.Db,TestContext.Current.CancellationToken));
        Assert.Contains("RP-ROLE-003",exception.Message);
        Assert.Equal(1,await Count(fixture,"SELECT count(*) FROM \"AspNetRoles\" WHERE \"Name\"='FfpReviewer'"));
    }

    private static RequestPortalAccounts Accounts(PostgreSqlTestDatabase fixture){var stores=new RequestPortalAccounts(fixture.Db.Database.GetConnectionString()!);stores.EnsureSchema();return stores;}
    private static VerifiedIdentity Proof(string name)=>new(new ExternalIdentityKey("staff-oidc","tenant",name),AuthenticationProviderKind.EntraId,name,name,DateTimeOffset.UtcNow,"verified-fixture");
    private static SecurityAccount Staff(RequestPortalAccounts accounts,string name)=>accounts.Staff.Resolve(Proof(name),AccountPopulation.Staff,true).Account;
    private static ClaimsPrincipal Actor(SecurityAccount account)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,account.Id),new Claim(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue)],"fixture"));
    private static RequestPortalAuthorizationStore Store(PostgreSqlTestDatabase fixture)=>new(fixture.Db,Options.Create(new ActiveDirectoryOptions()));
    private static async Task<Guid> LegacyRole(PostgreSqlTestDatabase fixture,string name,string? equivalent)
    {
        Guid id=Guid.NewGuid();await Sql(fixture,"INSERT INTO \"AspNetRoles\"(\"Id\",\"Name\",\"NormalizedName\") VALUES(@id,@name,@normalized)",("id",id),("name",name),("normalized",name.ToUpperInvariant()));
        if(equivalent is not null) await Sql(fixture,"INSERT INTO requestportal_role_permissions SELECT @name,permission_key FROM requestportal_role_permissions WHERE role_key=@equivalent",("name",name),("equivalent",equivalent));
        return id;
    }
    private static Task Assign(PostgreSqlTestDatabase fixture,string user,Guid role)=>Sql(fixture,"INSERT INTO \"AspNetUserRoles\" VALUES(@user,@role)",("user",Guid.Parse(user)),("role",role));
    private static async Task Sql(PostgreSqlTestDatabase fixture,string sql,params (string Key,object Value)[] parameters)
    {
        await using var connection=new NpgsqlConnection(fixture.Db.Database.GetConnectionString());await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command=new NpgsqlCommand(sql,connection);foreach(var p in parameters)command.Parameters.AddWithValue(p.Key,p.Value);await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
    private static async Task<long> Count(PostgreSqlTestDatabase fixture,string sql)
    {
        await using var connection=new NpgsqlConnection(fixture.Db.Database.GetConnectionString());await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command=new NpgsqlCommand(sql,connection);return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }
}
