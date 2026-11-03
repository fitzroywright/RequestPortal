using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Integration;
using Common.Security.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Common.Security;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;
using Xunit;

namespace RequestPortal.Infrastructure.Tests;

public sealed class StaffSignInIdentityCompatibilityTests
{
    private const string Tenant = "257d294c-c8a7-4b86-b791-6755edc4fe0a";
    private const string Issuer = "https://login.microsoftonline.com/" + Tenant + "/v2.0";
    private const string ObjectId = "5c6e1d4e-a815-4c8e-bcd4-c7bf6902f6f7";
    private static readonly ExternalIdentityRegistration Registration = new("requestportal-staff", Issuer, AuthenticationProviderKind.EntraId, [Tenant]);
    private static ClaimsPrincipal Principal(string subject = "existing-pairwise-subject", string tenant = Tenant) => new(new ClaimsIdentity(
        [new Claim("sub", subject), new Claim("tid", tenant), new Claim("oid", ObjectId),
         new Claim("preferred_username", "staff@example.org"), new Claim("email", "staff@example.org")], "validated-oidc"));
    private static VerifiedIdentity Canonical(ClaimsPrincipal principal) => VerifiedExternalIdentityFactory.FromValidatedPrincipal(principal, Registration);
    private static VerifiedIdentity Legacy(ClaimsPrincipal principal) => VerifiedExternalIdentityFactory.FromValidatedPrincipal(principal,
        Registration with { Kind = AuthenticationProviderKind.External, AllowedTenants = null });
    private static RequestPortalAccounts Accounts(PostgreSqlTestDatabase database)
    {
        var accounts = new RequestPortalAccounts(database.Db.Database.GetConnectionString()!);
        accounts.EnsureSchema();
        return accounts;
    }
    private static Task<long> AuditCount(PostgreSqlTestDatabase database) => database.Db.Database.SqlQueryRaw<long>(
        "SELECT count(*) AS \"Value\" FROM common_security_account_events").SingleAsync(TestContext.Current.CancellationToken);

    [Theory]
    [InlineData(SecurityAccountStatus.Active, "Administrator")]
    [InlineData(SecurityAccountStatus.Active, "Reviewer")]
    [InlineData(SecurityAccountStatus.PendingAccess, "Reviewer")]
    [InlineData(SecurityAccountStatus.Suspended, "Reviewer")]
    [InlineData(SecurityAccountStatus.Disabled, "Reviewer")]
    public async Task ExistingIssuerBindingsKeepAccountRolesAndLifecycleWithoutIdentityWrites(SecurityAccountStatus status, string role)
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts = Accounts(database); var principal = Principal(); var legacy = Legacy(principal);
        var account = accounts.Staff.Resolve(legacy, AccountPopulation.Staff, true).Account;
        accounts.Staff.SetStatus(account.Id, status, "approved-test-fixture");
        await database.Db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "AspNetUserRoles"("UserId","RoleId") SELECT {0},"Id" FROM "AspNetRoles" WHERE "Name"={1}
            """, [Guid.Parse(account.Id), role], TestContext.Current.CancellationToken);
        long before = await AuditCount(database);
        long version = accounts.Staff.Find(account.Id)!.SessionVersion;
        var selected = StaffSignInIdentityCompatibility.Select(principal, Registration, Canonical(principal), accounts);
        Assert.Equal(legacy.Key, selected.Key);
        Assert.Equal(before, await AuditCount(database));
        Assert.Equal(version, accounts.Staff.Find(account.Id)!.SessionVersion);
        var store = new RequestPortalAuthorizationStore(database.Db, Options.Create(new ActiveDirectoryOptions()));
        var result = await new StaffOidcPrincipalService(accounts, new PermissionAuthorizer(store))
            .BuildPrincipalAsync(selected, TestContext.Current.CancellationToken);
        Assert.Equal(status is not (SecurityAccountStatus.Suspended or SecurityAccountStatus.Disabled), result.Allowed);
        if (result.Allowed)
        {
            Assert.NotNull(result.Principal);
            Assert.Equal(account.Id, result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.Equal(status == SecurityAccountStatus.Active, result.Principal.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.Access));
            Assert.Equal(status == SecurityAccountStatus.Active && role == "Administrator",
                result.Principal.HasClaim(StaffOidcOptions.PermissionClaim, RequestPortalPermissions.ManageSecurity));
        }
        Assert.Single(accounts.Staff.List());
        Assert.Equal(legacy.Key, Assert.Single(accounts.Staff.GetIdentities(account.Id)).Key);
        Assert.Equal(status, accounts.Staff.Find(account.Id)!.Status);
        Assert.Equal([role], await store.GetDirectRoleKeysAsync(account.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreProvisionedTenantObjectBindingIsRecognizedWithoutAddingLegacyBinding()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts = Accounts(database); var principal = Principal(); var canonical = Canonical(principal);
        var account = accounts.Staff.Create(new(Guid.NewGuid().ToString("D"), canonical.LoginName, "Approved staff",
            AuthenticationProviderKind.EntraId, AccountPopulation.Staff, SecurityAccountStatus.PendingAccess, canonical.Key), "test-admin");
        long before = await AuditCount(database);
        var selected = StaffSignInIdentityCompatibility.Select(principal, Registration, canonical, accounts);
        Assert.Equal(canonical.Key, selected.Key);
        Assert.Equal(account.Id, accounts.Staff.Resolve(selected, AccountPopulation.Staff, false).Account.Id);
        Assert.Equal(before, await AuditCount(database));
        Assert.Equal(canonical.Key, Assert.Single(accounts.Staff.GetIdentities(account.Id)).Key);
    }

    [Fact]
    public async Task ConflictingImmutableBindingsFailClosedWithoutMergingOrChangingRecords()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts = Accounts(database); var principal = Principal(); var canonical = Canonical(principal);
        accounts.Staff.Resolve(Legacy(principal), AccountPopulation.Staff, true);
        accounts.Staff.Create(new(Guid.NewGuid().ToString("D"), "another-login", "Different existing account",
            AuthenticationProviderKind.EntraId, AccountPopulation.Staff, SecurityAccountStatus.Active, canonical.Key), "test-admin");
        long before = await AuditCount(database);
        Assert.Throws<InvalidOperationException>(() => StaffSignInIdentityCompatibility.Select(principal, Registration, canonical, accounts));
        Assert.Equal(before, await AuditCount(database)); Assert.Equal(2, accounts.Staff.List().Count);
    }

    [Fact]
    public async Task SameProfileNeverSubstitutesForExactImmutableBinding()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts = Accounts(database); var principal = Principal(); var canonical = Canonical(principal);
        accounts.Staff.Resolve(Legacy(Principal("different-provider-subject")), AccountPopulation.Staff, true);
        Assert.Equal(canonical.Key, StaffSignInIdentityCompatibility.Select(principal, Registration, canonical, accounts).Key);
    }

    [Fact]
    public async Task ExistingApprovedAliasesOnSameAccountNeedNoMutation()
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts = Accounts(database); var principal = Principal(); var canonical = Canonical(principal);
        var account = accounts.Staff.Resolve(Legacy(principal), AccountPopulation.Staff, true).Account;
        accounts.Staff.Link(account.Id, canonical, "approved-test-admin");
        long before = await AuditCount(database);
        Assert.Equal(canonical.Key, StaffSignInIdentityCompatibility.Select(principal, Registration, canonical, accounts).Key);
        Assert.Equal(before, await AuditCount(database)); Assert.Equal(2, accounts.Staff.GetIdentities(account.Id).Count);
    }

    [Theory]
    [InlineData("unallowed-tenant")]
    [InlineData("unauthenticated")]
    [InlineData("ambiguous-subject")]
    public async Task InvalidProofCannotUseLegacyCompatibility(string mode)
    {
        await using var database = await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var accounts = Accounts(database); var canonical = Canonical(Principal()); var principal = Principal();
        if (mode == "unallowed-tenant") principal = Principal(tenant: Guid.NewGuid().ToString("D"));
        if (mode == "unauthenticated") principal = new(new ClaimsIdentity(principal.Claims));
        if (mode == "ambiguous-subject") ((ClaimsIdentity)principal.Identity!).AddClaim(new("sub", "another-subject"));
        Assert.Throws<InvalidOperationException>(() => StaffSignInIdentityCompatibility.Select(principal, Registration, canonical, accounts));
        Assert.Empty(accounts.Staff.List());
    }
}
