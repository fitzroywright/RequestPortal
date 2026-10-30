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
        return global::Common.Security.Integration.VerifiedExternalIdentityFactory.FromValidatedOAuthPrincipal(info.Principal,
            new ExternalIdentityKey(info.LoginProvider,Scope(info.LoginProvider),info.ProviderKey));
    }
}
