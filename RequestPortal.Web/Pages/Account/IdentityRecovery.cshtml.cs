using Common.Security.Abstractions;
using Common.Security.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using RequestPortal.Infrastructure.Identity;
using RequestPortal.Web.Security;

namespace RequestPortal.Web.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting(AccountSecurityHosting.LoginRateLimit)]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class IdentityRecoveryModel(ApplicantIdentityAdministration recovery, SignInManager<ApplicationUser> signIn,
    UserManager<ApplicationUser> users) : PageModel
{
    [BindProperty] public string Token { get; set; }="";
    public string? Error { get; private set; }
    public void OnGet() => ProtectResponse();
    public async Task<IActionResult> OnPostAsync()
    {
        ProtectResponse();
        var invitation=await recovery.InvitationAsync(Token,HttpContext.RequestAborted);
        if(invitation is null) return InvalidInvitation();
        if(!(await recovery.ProvidersAsync()).Contains(invitation.Provider)) { Error="The approved provider is unavailable. Contact support.";Response.StatusCode=400;return Page(); }
        var properties=signIn.ConfigureExternalAuthenticationProperties(invitation.Provider,Url.Page("/Account/IdentityRecovery","Callback")!,invitation.RecoveryId.ToString());
        properties.Items["RequestPortal.IdentityRecovery"]=Token;
        return Challenge(properties,invitation.Provider);
    }
    public async Task<IActionResult> OnGetCallbackAsync(string? remoteError=null)
    {
        ProtectResponse();
        var external=await HttpContext.AuthenticateAsync(IdentityConstants.ExternalScheme);
        string? token=null;external.Properties?.Items.TryGetValue("RequestPortal.IdentityRecovery",out token);
        if(remoteError is not null || token is null) return await InvalidCallback("RP-RECOVERY-CALLBACK");
        var invitation=await recovery.InvitationAsync(token,HttpContext.RequestAborted);
        if(invitation is null) return await InvalidCallback("RP-RECOVERY-INVITATION");
        var info=await signIn.GetExternalLoginInfoAsync(invitation.RecoveryId.ToString());
        if(info is null) return await InvalidCallback("RP-RECOVERY-CORRELATION");
        try
        {
            var proof=PublicExternalIdentity.FromCallback(info);
            var recovered=await recovery.CompleteAsync(token,proof,HttpContext.RequestAborted);
            var user=await users.FindByIdAsync(recovered.Id.ToString());
            if(user is null || user.IsInternalStaff) return Forbid();
            var account=await RequireActive(user);
            if(account is null) return Forbid();
            user.LastSignInUtc=DateTimeOffset.UtcNow;
            if(!(await users.UpdateAsync(user)).Succeeded) throw new InvalidOperationException("The sign-in record could not be updated.");
            await signIn.SignInWithClaimsAsync(user,false,AccountSecurityHosting.SessionClaims(account,proof));
            await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);
            TempData["Success"]="Your verified sign-in method is now linked to your existing account.";
            return RedirectToPage("/Dashboard/Index");
        }
        catch(InvalidOperationException) { return await InvalidCallback("RP-RECOVERY-LINK"); }
    }
    private Task<SecurityAccount?> RequireActive(ApplicationUser user)
    {
        var accounts=HttpContext.RequestServices.GetRequiredService<RequestPortalAccounts>();
        var account=accounts.Public.Find(user.Id.ToString());
        return Task.FromResult(!user.IsInternalStaff && account is {Status:SecurityAccountStatus.Active,Population:AccountPopulation.PublicApplicant}?account:null);
    }
    private IActionResult InvalidInvitation() { Error="This invitation is invalid, expired, cancelled, or already used. Contact support for a new independently approved invitation.";Response.StatusCode=400;return Page(); }
    private async Task<IActionResult> InvalidCallback(string code) { await HttpContext.SignOutAsync(IdentityConstants.ExternalScheme);InvalidInvitation();Error += " Support reference: " + code;return Page(); }
    private void ProtectResponse() { Response.Headers.CacheControl="no-store";Response.Headers["Referrer-Policy"]="no-referrer"; }
}
