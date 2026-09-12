# FFP Staff Authentication Contract

Request Portal does not authenticate `fftpj.local` passwords. Staff authentication is delegated to a standards-compliant OpenID Connect provider. Keycloak is the initial provider, but Request Portal contains no Keycloak-specific API or protocol dependency.

## Request Portal flow

1. A visitor selects **FFP Staff Sign-In**.
2. Request Portal issues an OIDC Authorization Code challenge to the configured `Authentication:StaffOidc:Authority`.
3. PKCE is required and Request Portal requests `prompt=login`; silent SSO is not used.
4. The identity provider authenticates the staff member against FFP Active Directory and returns an OIDC authorization code.
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

## Keycloak initial deployment

For the first deployment, configure Keycloak with LDAP/Active Directory user federation against `fftpj.local`. Configure an OIDC confidential client for Request Portal and map the required identity/group claims into the token or user-info response.

Request Portal needs only the generic OIDC values:

- `Authentication:StaffOidc:Authority`
- `Authentication:StaffOidc:ClientId`
- `Authentication:StaffOidc:ClientSecret`
- `Authentication:StaffOidc:CallbackPath`

Store `Authentication:StaffOidc:ClientSecret` in OpenBao. Do not place it in source-controlled configuration.

The registered redirect URI must match the Request Portal origin plus `/signin-ffp-staff`.

## Authorization

Authentication and authorization are separate. Provider groups are mapped to roles by Common.Security, and roles map to permissions in Request Portal. Dashboard routes are protected server-side by `ManagementDashboard.View`; UI visibility is not treated as an authorization control.

## Security invariants

- No AD password fields exist on the Request Portal staff sign-in surface.
- No LDAP bind is performed by the Request Portal staff login flow.
- No Entra ID or Microsoft 365 dependency is required.
- OIDC Authorization Code Flow with PKCE is used.
- `prompt=login` prevents silent provider SSO for this flow.
- OIDC client secrets are supplied through Common.Secrets/OpenBao in production.
- Public and staff authorization surfaces are distinguished by an explicit staff-authentication claim and separate server-side policies.
