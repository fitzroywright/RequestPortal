using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Hosting;
using Common.Security.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RequestPortal.Infrastructure.Security;
using RequestPortal.Web.Security;
using Xunit;
using ApplicationUser=RequestPortal.Infrastructure.Identity.ApplicationUser;

namespace RequestPortal.Infrastructure.Tests;

public sealed class IdentityModernizationTests
{
    [Theory]
    [InlineData("Microsoft")]
    [InlineData("Google")]
    [InlineData("Yahoo")]
    public void ProtectedExternalCallbackUsesProviderSubjectInsteadOfEmail(string provider)
    {
        var principal=new ClaimsPrincipal(new ClaimsIdentity(new[] {new Claim(ClaimTypes.Email,"contact@example.test"),new Claim(ClaimTypes.Name,"Applicant")},provider));
        var proof=PublicExternalIdentity.FromCallback(new ExternalLoginInfo(principal,provider,"stable-subject",provider));
        Assert.Equal("stable-subject",proof.Key.SubjectId);Assert.Equal(provider,proof.Key.ProviderId);
        Assert.Null(proof.VerifiedEmail);Assert.Equal("contact@example.test",proof.Claims["contact_email"]);
        var another=PublicExternalIdentity.FromCallback(new ExternalLoginInfo(principal,provider,"different-subject",provider));
        Assert.NotEqual(proof.Key,another.Key);
    }
    [Fact]
    public void UnauthenticatedExternalCallbackCannotCreateProviderProof()
    {
        var info=new ExternalLoginInfo(new ClaimsPrincipal(new ClaimsIdentity()),"Google","subject","Google");
        Assert.Throws<InvalidOperationException>(()=>PublicExternalIdentity.FromCallback(info));
    }
    [Fact]
    public async Task PublicFirstLoginCreatesActiveBusinessIdentityInTheSameTransaction()
    {
        await using var db=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);var accounts=Accounts(db);
        var proof=Proof("Google","subject","new@example.test");
        var account=accounts.Public.Resolve(proof,AccountPopulation.PublicApplicant,true).Account;
        Assert.Equal(SecurityAccountStatus.Active,account.Status);
        var user=await db.Db.Users.SingleAsync(x=>x.Id==Guid.Parse(account.Id),TestContext.Current.CancellationToken);Assert.False(user.IsInternalStaff);
        Assert.True(user.IndividualProfileUpdateRequired);Assert.Equal("new@example.test",user.Email);
        Assert.Equal(account.Id,accounts.Public.Resolve(proof,AccountPopulation.PublicApplicant,true).Account.Id);
        Assert.Single(await db.Db.Users.ToListAsync(TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task MatchingEmailCannotLinkAnExistingApplicantAndRollsBackProvisioning()
    {
        await using var db=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);var accounts=Accounts(db);
        var existing=User("existing@example.test");db.Db.Users.Add(existing);await db.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Throws<IdentityEnrollmentRequiredException>(()=>accounts.Public.Resolve(Proof("Google","attacker","existing@example.test"),AccountPopulation.PublicApplicant,true));
        Assert.Empty(accounts.Public.List());Assert.Equal(existing.Id,(await db.Db.Users.SingleAsync(TestContext.Current.CancellationToken)).Id);
    }
    [Fact]
    public async Task StaffWithApplicantContactEmailCannotConvertTheApplicantOrGainAccess()
    {
        await using var db=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);var accounts=Accounts(db);
        var applicant=User("person@example.test");db.Db.Users.Add(applicant);await db.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var proof=new VerifiedIdentity(new ExternalIdentityKey("staff-oidc","tenant","staff-subject"),AuthenticationProviderKind.EntraId,
            "person@example.test","Staff",DateTimeOffset.UtcNow,"oidc","person@example.test",["Administrators"]);
        var roles=new RequestPortalAuthorizationStore(db.Db,Options.Create(new ActiveDirectoryOptions()));
        var service=new StaffOidcPrincipalService(accounts,new PermissionAuthorizer(roles));
        var result=await service.BuildPrincipalAsync(proof,TestContext.Current.CancellationToken);
        Assert.True(result.Allowed);Assert.NotNull(result.Principal);
        Assert.False(result.Principal.HasClaim(StaffOidcOptions.PermissionClaim,RequestPortalPermissions.Access));
        Assert.NotEqual(applicant.Id.ToString(),result.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.False((await db.Db.Users.SingleAsync(x=>x.Id==applicant.Id,TestContext.Current.CancellationToken)).IsInternalStaff);
        Assert.Equal(SecurityAccountStatus.PendingAccess,Assert.Single(accounts.Staff.List()).Status);
    }
    [Fact]
    public async Task ApprovedMigrationPreservesOriginalApplicantIdAndAllProviderBindings()
    {
        await using var db=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);var accounts=Accounts(db);
        var user=User("original@example.test");db.Db.Users.Add(user);await db.Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var google=Proof("Google","original-google",user.Email!);var microsoft=Proof("Microsoft","original-ms",user.Email!);
        var row=new AccountCreation(user.Id.ToString("D"),user.UserName!,user.DisplayName,AuthenticationProviderKind.External,
            AccountPopulation.PublicApplicant,SecurityAccountStatus.Active,google.Key,[microsoft.Key]);
        accounts.Public.ImportApprovedAccounts([row],"approved-migration");accounts.Public.ImportApprovedAccounts([row],"approved-migration");
        Assert.Equal(user.Id.ToString("D"),accounts.Public.Resolve(microsoft,AccountPopulation.PublicApplicant,true).Account.Id);
        Assert.Single(await db.Db.Users.ToListAsync(TestContext.Current.CancellationToken));Assert.Equal(2,accounts.Public.GetIdentities(row.Id).Count);
    }
    [Fact]
    public async Task ConcurrentPublicCallbacksProduceOneApplicantAccount()
    {
        await using var db=await PostgreSqlTestDatabase.CreateAsync(TestContext.Current.CancellationToken);var accounts=Accounts(db);
        var proof=Proof("Yahoo","same-subject","concurrent@example.test");
        var results=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>Task.Run(()=>accounts.Public.Resolve(proof,AccountPopulation.PublicApplicant,true),TestContext.Current.CancellationToken)));
        Assert.Single(results.Select(x=>x.Account.Id).Distinct());Assert.Single(await db.Db.Users.ToListAsync(TestContext.Current.CancellationToken));
    }
    private static RequestPortalAccounts Accounts(PostgreSqlTestDatabase db)
    {var accounts=new RequestPortalAccounts(db.Db.Database.GetConnectionString()!);accounts.EnsureSchema();return accounts;}
    private static VerifiedIdentity Proof(string provider,string subject,string email)=>new(new ExternalIdentityKey(provider,PublicExternalIdentity.Scope(provider),subject),AuthenticationProviderKind.External,
        email,"Applicant",DateTimeOffset.UtcNow,"validated-oauth",claims:new Dictionary<string,string>{{"contact_email",email}});
    private static ApplicationUser User(string email)=>new() {Id=Guid.NewGuid(),UserName=email,NormalizedUserName=email.ToUpperInvariant(),Email=email,NormalizedEmail=email.ToUpperInvariant(),DisplayName="Existing Applicant",CreatedUtc=DateTimeOffset.UtcNow,SecurityStamp=Guid.NewGuid().ToString("N")};
}
