using System.Security.Claims;
using Common.Security.Abstractions;
using Common.Security.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

[Authorize(Policy="PortalRepresentative")]
public sealed class LinkedIdentitiesModel(RequestPortalAccounts accounts,SignInManager<ApplicationUser> signIn,UserManager<ApplicationUser> users,IAuthenticationSchemeProvider schemes):PageModel
{
    public IReadOnlyList<LinkedAuthenticationIdentity> Identities {get;private set;}=[];
    public string[] Providers {get;private set;}=[];
    public async Task OnGetAsync()
    {
        Identities=accounts.Public.GetIdentities(AccountId());
        Providers=(await schemes.GetAllSchemesAsync()).Where(x=>new[]{"Google","Microsoft","Yahoo","Apple","Facebook"}.Contains(x.Name)).Select(x=>x.Name).ToArray();
    }
    public async Task<IActionResult> OnPostLinkAsync(string provider)
    {
        if(!Fresh()) return RedirectToPage("/Account/Login",new{returnUrl="/Account/LinkedIdentities"});
        var allowed=(await schemes.GetAllSchemesAsync()).Any(x=>x.Name==provider && new[]{"Google","Microsoft","Yahoo","Apple","Facebook"}.Contains(provider));
        if(!allowed)return BadRequest();
        var properties=signIn.ConfigureExternalAuthenticationProperties(provider,Url.Page("/Account/LinkedIdentities","Callback")!,AccountId());
        return Challenge(properties,provider);
    }
    public async Task<IActionResult> OnGetCallbackAsync(string? remoteError=null)
    {
        if(!Fresh() || remoteError is not null)return BadRequest("Fresh authentication is required to link an identity.");
        string id=AccountId();var info=await signIn.GetExternalLoginInfoAsync(id);if(info is null)return BadRequest("Invalid identity-link callback.");
        var proof=PublicExternalIdentity.FromCallback(info);
        try {accounts.Public.Link(id,proof,id);}
        catch(IdentityAlreadyLinkedException) {TempData["Error"]="This identity already belongs to another account.";return RedirectToPage();}
        var account=accounts.Public.Find(id)!;var user=await users.FindByIdAsync(id);
        if(user is null || user.IsInternalStaff || account.Status!=SecurityAccountStatus.Active)return Forbid();
        await signIn.SignInWithClaimsAsync(user,false,PublicExternalIdentity.SessionClaims(account,proof));
        await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostUnlinkAsync(string providerId,string scopeId,string subjectId)
    {
        if(!Fresh())return BadRequest("Sign in again before changing linked identities.");
        string id=AccountId();
        string[] configured=(await schemes.GetAllSchemesAsync()).Where(x=>new[]{"Google","Microsoft","Yahoo","Apple","Facebook"}.Contains(x.Name)).Select(x=>x.Name).ToArray();
        try { await accounts.UnlinkPublicIdentityAsync(User,new global::Common.Security.Models.ExternalIdentityKey(providerId,scopeId,subjectId),configured,HttpContext.RequestAborted); }
        catch(Exception error) when(error is InvalidOperationException or UnauthorizedAccessException) { return BadRequest(error.Message); }
        await signIn.SignOutAsync();return RedirectToPage("/Account/Login");
    }
    private string AccountId()=>User.FindFirstValue(AccountSecurityHosting.AccountIdClaim) ?? throw new InvalidOperationException("An applicant identity is required.");
    private bool Fresh()=>long.TryParse(User.FindFirstValue(AccountSecurityHosting.AuthenticationTimeClaim),out long seconds) &&
        DateTimeOffset.FromUnixTimeSeconds(seconds)>=DateTimeOffset.UtcNow.AddMinutes(-10) && DateTimeOffset.FromUnixTimeSeconds(seconds)<=DateTimeOffset.UtcNow.AddMinutes(2);
}
