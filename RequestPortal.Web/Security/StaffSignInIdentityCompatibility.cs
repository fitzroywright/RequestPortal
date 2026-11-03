using System.Security.Claims;
using Common.Security.Integration;
using Common.Security.Models;

namespace RequestPortal.Web.Security;

/// <summary>Resolves only immutable bindings proven by the same validated OIDC callback. Never links or rewrites identities.</summary>
public static class StaffSignInIdentityCompatibility
{
    public static VerifiedIdentity Select(ClaimsPrincipal validatedPrincipal, ExternalIdentityRegistration registration,
        VerifiedIdentity canonical, RequestPortalAccounts accounts)
    {
        if (registration.Kind != AuthenticationProviderKind.EntraId) return canonical;
        // The OIDC handler has validated signature, issuer, audience, lifetime and protocol correlation.
        // Validate tid/oid before considering the historical issuer/sub representation of this exact token.
        var verified = VerifiedExternalIdentityFactory.FromValidatedPrincipal(validatedPrincipal, registration);
        if (verified.Key != canonical.Key || canonical.ProviderKind != AuthenticationProviderKind.EntraId)
            throw new InvalidOperationException("The staff identity proofs disagree.");
        var legacy = VerifiedExternalIdentityFactory.FromValidatedPrincipal(validatedPrincipal,
            registration with { Kind = AuthenticationProviderKind.External, AllowedTenants = null });
        var canonicalAccount = accounts.FindStaffBinding(canonical.Key);
        var legacyAccount = accounts.FindStaffBinding(legacy.Key);
        if (canonicalAccount is not null && legacyAccount is not null && canonicalAccount != legacyAccount)
            throw new InvalidOperationException("The staff identity has conflicting existing bindings. Administrative review is required.");
        // Preserve historical binding and account population/status/roles without matching username or email.
        // Newly pre-provisioned identities use canonical tenant/object keys on their first sign-in.
        return legacyAccount is not null && canonicalAccount is null ? legacy : canonical;
    }
}
