using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Authorization;
using Common.Security.Hosting;
using Common.Security.Models;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;

namespace RequestPortal.Web.Security;

public sealed record StaffOidcPrincipalResult(bool Allowed, ClaimsPrincipal? Principal, string? FailureCode=null);

/// <summary>Request Portal staff permission projection. Provider proof and lifecycle are shared framework concerns.</summary>
public sealed class StaffOidcPrincipalService(RequestPortalAccounts accounts, IPermissionAuthorizer authorizer)
{
    private static readonly string[] permissions = ["requestportal.access", "requests.review", "ManagementDashboard.View", "security.manage"];
    public async Task<StaffOidcPrincipalResult> BuildPrincipalAsync(VerifiedIdentity proof, CancellationToken cancellationToken=default)
    {
        var account = new global::Common.Security.Services.AccountProvisioningService(accounts.Staff)
            .Provision(proof, AccountPopulation.Staff, true).Account;
        if (account.Status is SecurityAccountStatus.Disabled or SecurityAccountStatus.Suspended)
            return new(false,null,"ACCOUNT_DISABLED");
        accounts.RecordStaffSignIn(account, proof);
        List<Claim> claims = [new(ClaimTypes.NameIdentifier,account.Id), new(ClaimTypes.Name,account.DisplayName),
            new(StaffOidcOptions.StaffSurfaceClaim,StaffOidcOptions.StaffSurfaceValue)];
        claims.AddRange(AccountSecurityHosting.SessionClaims(account,proof));
        claims.AddRange(proof.GroupNames.Select(group => new Claim("directory_group",group)));
        await AddPermissions(claims,account,cancellationToken);
        return new(true,new ClaimsPrincipal(new ClaimsIdentity(claims,StaffOidcOptions.StaffCookieScheme)));
    }
    public async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        await AccountSecurityHosting.ValidateCookieAsync(context,accounts.Staff);
        if (context.Principal?.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated || context.Principal is null) return;
        var id=context.Principal.FindFirstValue(AccountSecurityHosting.AccountIdClaim);
        var account=id is null ? null : accounts.Staff.Find(id);
        if (account is null || context.Principal.FindFirstValue(ClaimTypes.NameIdentifier)!=account.Id) { context.RejectPrincipal(); return; }
        var claims=identity.Claims.Where(c => c.Type!=ClaimTypes.Role && c.Type!=StaffOidcOptions.PermissionClaim).ToList();
        await AddPermissions(claims,account,context.HttpContext.RequestAborted);
        context.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(claims,StaffOidcOptions.StaffCookieScheme)));
    }
    private async Task AddPermissions(List<Claim> claims,SecurityAccount account,CancellationToken token)
    {
        if(account.Status!=SecurityAccountStatus.Active) return;
        var subject=AuthorizationSubject.Create(account.Id,claims.Where(c=>c.Type=="directory_group").Select(c=>c.Value));
        foreach(var permission in permissions)
            if((await authorizer.AuthorizeAsync(subject,permission,token)).Allowed)
                claims.Add(new(StaffOidcOptions.PermissionClaim,permission));
    }
}
