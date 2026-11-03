# FFP Staff Authentication Contract

Request Portal does not authenticate `fftpj.local` passwords. Production staff authentication uses Microsoft Entra ID through standards-compliant OpenID Connect, followed by Common.Security authorization. The generic OIDC abstraction remains reusable.

## Request Portal flow

1. A visitor selects **FFP Staff Sign-In**.
2. Request Portal issues an OIDC Authorization Code challenge to the configured `Authentication:StaffOidc:Authority`.
3. PKCE is required and Request Portal requests `prompt=login`; silent SSO is not used.
4. Microsoft Entra ID authenticates the staff member and returns an OIDC authorization code.
5. Request Portal completes the standards-based code exchange. It never sees the staff member's AD password.
6. The provider identity and group claims are evaluated through Common.Security.
7. Only staff with `requestportal.access` receive a Request Portal staff principal.
8. The Management Dashboard additionally requires `ManagementDashboard.View`.
9. Authorized management users are sent directly to `/Management`.

Public Request Portal identity remains separate and unchanged.

## Required provider claims

The claim names are configurable. Defaults are:

- Username: `preferred_username`
- Display name: `name`
- Email: `email`
- AD group memberships: one or more of `groups`, `group`, `memberOf`

The username can be `FWright`; Request Portal normalizes domain-qualified or UPN values but does not require callers to enter `FFTPJ\\FWright`.

The provider must emit the AD group names configured under:

- `Authentication:ActiveDirectory:ReviewerGroups`
- `Authentication:ActiveDirectory:AdministratorGroups`

These names are authorization inputs only. AD credential validation belongs to the identity provider.

## Microsoft Entra production deployment

Use the existing tenant `257d294c-c8a7-4b86-b791-6755edc4fe0a`. Both `Authentication:StaffOidc:Authority` and `Issuer` are `https://login.microsoftonline.com/257d294c-c8a7-4b86-b791-6755edc4fe0a/v2.0`; `AllowedTenantIds` must include that tenant. Common.Security binds the validated `tid` and `oid` claims rather than the pairwise `sub` or email.

Preserve the existing OIDC `ClientId`, `CallbackPath` and Common.Secrets/OpenBao client secret. The registered redirect URI must match `https://requestportal.ffpja.org/signin-ffp-staff`. Do not place credentials in source-controlled configuration.

Directory pre-provisioning uses a separate dedicated Microsoft Graph application with read-only `User.Read.All` application permission and tenant administrator consent. See [Staff pre-provisioning](STAFF-PREPROVISIONING.md) for configuration and credential commissioning. Missing directory credentials do not disable existing staff sign-in.

## Authorization

Authentication and authorization are separate. Provider groups are mapped to roles by Common.Security, and roles map to permissions in Request Portal. Dashboard routes are protected server-side by `ManagementDashboard.View`; UI visibility is not treated as an authorization control.

## Security invariants

- No AD password fields exist on the Request Portal staff sign-in surface.
- No LDAP bind is performed by the Request Portal staff login flow.
- Staff sign-in uses Entra; directory provisioning uses read-only Graph calls and never creates an Entra account.
- OIDC Authorization Code Flow with PKCE is used.
- `prompt=login` prevents silent provider SSO for this flow.
- OIDC client secrets are supplied through Common.Secrets/OpenBao in production.
- Public and staff authorization surfaces are distinguished by an explicit staff-authentication claim and separate server-side policies.
