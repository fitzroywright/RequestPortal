using System.Security.Claims;
using Common.Security.Models;
using Microsoft.AspNetCore.Identity;

namespace RequestPortal.Web.Security;

public static class PublicExternalIdentity
{
    public static string Scope(string provider) => provider switch {
        "Google" => "https://accounts.google.com", "Microsoft" => "https://graph.microsoft.com",
        "Yahoo" => "https://api.login.yahoo.com", "Facebook" => "https://www.facebook.com",
        "Apple" => "https://appleid.apple.com", _ => throw new InvalidOperationException("Unregistered external provider.") };
    /// <summary>Input is obtained only from SignInManager's protected, correlated external callback cookie.</summary>
    public static VerifiedIdentity FromCallback(ExternalLoginInfo info)
    {
        if(info.Principal.Identity?.IsAuthenticated!=true) throw new InvalidOperationException("Unauthenticated external callback.");
        string? email=info.Principal.FindFirstValue(ClaimTypes.Email);
        string display=info.Principal.FindFirstValue(ClaimTypes.Name) ?? email ?? info.ProviderKey;
        var contact=string.IsNullOrWhiteSpace(email) ? new Dictionary<string,string>() : new Dictionary<string,string>{{"contact_email",email}};
        return new(new ExternalIdentityKey(info.LoginProvider,Scope(info.LoginProvider),info.ProviderKey),AuthenticationProviderKind.External,
            email ?? info.ProviderKey,display,DateTimeOffset.UtcNow,"validated-oauth-callback",
            info.Principal.FindFirstValue("email_verified")=="true" ? email : null,claims:contact);
    }
}
