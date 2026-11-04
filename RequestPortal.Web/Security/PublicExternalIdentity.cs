using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Common.Security.Abstractions;
using Common.Security.Hosting;
using System.Security.Claims;
using Common.Security.Models;
using Microsoft.AspNetCore.Identity;

namespace RequestPortal.Web.Security;

public static class PublicExternalIdentity
{
    public const string BindingClaim = "requestportal.public.binding";
    public static string BindingFingerprint(ExternalIdentityKey key) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { key.ProviderId, key.ScopeId, key.SubjectId }))));
    public static IEnumerable<Claim> SessionClaims(SecurityAccount account, VerifiedIdentity proof) =>
        AccountSecurityHosting.SessionClaims(account, proof).Append(new Claim(BindingClaim, BindingFingerprint(proof.Key)));

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
