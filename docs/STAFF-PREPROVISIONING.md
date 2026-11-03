# Staff provisioning before first sign-in

Administration → Staff & Roles → Add Staff lets an existing authorized staff administrator search the configured identity provider, review an enabled directory user, assign Administrator and/or Reviewer, and create Pending Access or explicitly Approve & Activate. This creates a Request Portal staff account only. It does not create directory users, passwords, sessions, public applicants or Organization Managers. Staff still authenticate normally through the configured OIDC provider.

The feature defaults to disabled. Request Portal previously had no directory lookup connection. An approved read-only directory client and its secret must be commissioned before enabling this feature. Missing configuration or provider read permissions result in an unavailable/error message; no unverified/manual identity entry is offered. Do not reuse a SharePoint, messaging, broad directory administrator or staff password credential.

## Configure the existing AD federation (Keycloak)

Use the Keycloak realm used by `Authentication:StaffOidc:Authority` and `Issuer`. A realm directory search includes users exposed by its configured AD/LDAP federation; Request Portal makes no LDAP bind. The directory must expose the intended user before their first portal sign-in.

Have the identity-provider owner register an approved confidential service client with client-credentials enabled and only the user query/view privileges required by GET `/admin/realms/{realm}/users` and GET `/users/{id}`. No manage-users, password-reset, role-management or realm-administration grants are needed. Use the provider's supported least-privilege user-view authorization model, and test search/detail with the actual account. The identity-provider owner must verify that this portal client's validated OIDC `sub` is the same immutable `UserRepresentation.id` returned by the user API. Pairwise/custom subject mappers are not supported by this adapter; never substitute the underlying LDAP objectGUID or email.

Using the existing protected configuration and OpenBao/Common.Secrets process, set:

```
Authentication__StaffDirectory__Enabled=true
Authentication__StaffDirectory__Provider=Keycloak
Authentication__StaffDirectory__ClientId=<approved-read-only-client>
Authentication__StaffDirectory__ClientSecretName=Authentication:StaffDirectory:ClientSecret
Authentication__StaffDirectory__KeycloakSubjectMappingVerified=true
```

`KeycloakSubjectMappingVerified` is an operator attestation after checking the provider's subject mapper, not a user-submitted field. Realm URLs derive from the existing configured HTTPS issuer, which must equal Authority. Request Portal cannot safely provision a subject that the provider cannot authoritatively return before sign-in.

API reference: https://www.keycloak.org/docs-api/latest/rest-api/index.html#_users

## Configure direct Microsoft Entra OIDC

This mode is for staff who sign in directly through the configured Entra provider. It must not be used to bind an Entra object ID to a federated Keycloak issuer. Register an approved read-only application client in an explicitly allowed tenant with Microsoft Graph application `User.Read.All`, with the tenant administrator's consent. This adapter reads `id`, `userPrincipalName`, `displayName`, `mail`, `accountEnabled`. It never requests write permissions or modifies Microsoft Graph users. In addition to Enabled, ClientId and ClientSecretName above, set:

```
Authentication__StaffDirectory__Provider=Entra
Authentication__StaffDirectory__TenantId=<approved-tenant-GUID>
```

`StaffOidc:AllowedTenantIds` must include that tenant; Authority and Issuer must identify that tenant at `https://login.microsoftonline.com/{tenant}` or `.../{tenant}/v2.0`, as applicable to the configured OIDC issuer. The identity key uses the tenant GUID and object GUID, exactly as Common.Security validates `tid` and `oid`; it never uses Entra's pairwise `sub`. The adapter currently searches one configured tenant. Other allowed sign-in tenants are unchanged.

API/permission reference: https://learn.microsoft.com/en-us/graph/api/user-list?view=graph-rest-1.0

## Security and persistence

All page handlers and service operations require active staff security administration; Razor Pages provides antiforgery protection. Search returns at most 21 results (20 displayed with a refine-search message), has a minimum query length, and uses escaped provider queries. No provider write endpoints are called. HTTPS is mandatory and HTTP redirects are disabled, including token requests. The directory secret is resolved on demand by reference through Common.Secrets; provider bodies and credentials are not shown in errors.

Selection tokens are encrypted/authenticated, expire after ten minutes, and bind the administrator, session version, provider configuration and immutable identity. Submission re-reads the selected identity from the directory and rejects disabled, deleted or changed identities. The browser cannot submit its own subject, provider, name or email. Contact email is directory profile information; it never authorizes account linking or claims email verification.

Common.Security owns account creation, identity uniqueness and the business-user projection. An atomic creation transaction checks the actor's current status/session version under a row lock, writes direct canonical roles and structured audit (actor, subject, time, new roles/status and verified binding). Any projection, role, authorization or audit failure rolls everything back. Revoked identity tombstones are respected. Existing accounts are returned without changing their lifecycle, roles, profile or bootstrap markers. Username conflicts with another binding require explicit investigation; email or name matches never merge accounts.

New pre-provisioned records have no last-sign-in timestamp and show “Never signed in.” First successful provider authentication resolves the existing immutable binding and records the actual sign-in. Pending accounts remain pending until approval; Active accounts receive only their assigned/direct and verified provider-group permissions. Suspensions, revocations, self-escalation and last-administrator protections continue through the existing administration workflow.

No database migration is needed: the existing Common.Security binding tables, nullable `AspNetUsers.LastSignInUtc`, roles and structured audit table suffice. Historical dates, account assignments and provider bindings are not rewritten.

## Validation and deployment

Run builds/tests with the pinned Common source manifest and disposable PostgreSQL. Do not point tests at production or enable `LiveFfpSql`. Validate administrator/reviewer/public isolation, provider identity-key matching, malicious/stale selections, duplicates, disabled identities, approval and first-sign-in reuse, atomic rollback and audit. Review Add Staff at desktop/mobile sizes.

Deploy only Request Portal with the existing Aegis.Operations tooling after PR checks pass. Preserve production environment/OpenBao configuration and data. Without the approved directory client the Add Staff page safely reports that provisioning is unavailable; deploying code does not enable directory access or add directory permissions. Verify an actual directory lookup and first sign-in in production only after its owner commissions that connection.
