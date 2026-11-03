# Staff provisioning before first sign-in

Administration → Staff & Roles → Add Staff lets an existing authorized staff administrator search the configured identity provider, review an enabled directory user, assign Administrator and/or Reviewer, and create Pending Access or explicitly Approve & Activate. This creates a Request Portal staff account only. It does not create directory users, passwords, sessions, public applicants or Organization Managers. Staff still authenticate normally through the configured OIDC provider.

The feature defaults to disabled. Request Portal previously had no directory lookup connection. An approved read-only directory client and its secret must be commissioned before enabling this feature. Missing configuration or provider read permissions result in an unavailable/error message; no unverified/manual identity entry is offered. Do not reuse a SharePoint, messaging, broad directory administrator or staff password credential.

## Configure Microsoft Entra directory access

The supported production architecture is **Microsoft Entra ID → Request Portal → Common.Security**. Staff authenticate directly with Entra. Directory lookup uses a separate, dedicated read-only application identity in the existing tenant `257d294c-c8a7-4b86-b791-6755edc4fe0a`.

An Entra administrator must approve the application, grant Microsoft Graph **application** permission `User.Read.All`, grant tenant administrator consent, and provide its credential through the approved secret-handling process. Prefer a dedicated directory identity; do not request directory write permissions or reuse messaging, SharePoint or unrelated application identities. The approved Kratos connection reuses the existing Request Portal staff application `45d755f5-3dd4-45b6-ae13-366b8122bb2b`, whose effective Graph application role was verified as `User.Read.All` only, with secret reference `Authentication:StaffOidc:ClientSecret`. No credential copy or rotation is needed. See [the scoped Kratos commissioning procedure](STAFF-DIRECTORY-KRATOS-COMMISSIONING.md), including preservation of historical issuer-scoped staff bindings. If consent or credentials are unavailable, leave directory provisioning disabled and continue independent deployment/testing work. Never invent credentials.

Store the credential as `Authentication:StaffDirectory:ClientSecret` in the existing production OpenBao/Common.Secrets namespace. Do not put the credential in environment files, source control, command arguments, logs or reports. Only its reference and public application ID belong in configuration. Use the existing protected credential provisioning process; preserve OpenBao authentication and database settings.

After validating the dedicated application and its consent, configure:

```ini
Authentication__StaffDirectory__Enabled=true
Authentication__StaffDirectory__Provider=Entra
Authentication__StaffDirectory__TenantId=257d294c-c8a7-4b86-b791-6755edc4fe0a
Authentication__StaffDirectory__ClientId=<approved-application-id>
Authentication__StaffDirectory__ClientSecretName=Authentication:StaffDirectory:ClientSecret
```

Verify `Authentication:StaffOidc:AllowedTenantIds` includes `257d294c-c8a7-4b86-b791-6755edc4fe0a`, and both Authority and Issuer match `https://login.microsoftonline.com/257d294c-c8a7-4b86-b791-6755edc4fe0a/v2.0`. Preserve the existing staff sign-in application and its secret. Generic OIDC remains reusable; the directory adapter supports Entra only and rejects other provider configurations before requesting credentials.

The immutable identity key uses provider ID, tenant GUID and object GUID, exactly as Common.Security validates `tid` and `oid`; it never uses Entra's pairwise `sub`, email or username for linking. Directory lookup searches one configured tenant and does not change other allowed sign-in tenants.

The adapter requests a client-credentials token with scope `https://graph.microsoft.com/.default`, then calls only GET `https://graph.microsoft.com/v1.0/users` and GET `/users/{object-id}`. It selects `id`, `userPrincipalName`, `displayName`, `mail`, `accountEnabled`; absent/false accountEnabled cannot be provisioned. Search escapes OData string literals and caps results. Missing consent, invalid credentials, deleted users and malformed responses never authorize provisioning.

Microsoft references: [list users](https://learn.microsoft.com/en-us/graph/api/user-list?view=graph-rest-1.0), [get user](https://learn.microsoft.com/en-us/graph/api/user-get?view=graph-rest-1.0).

## Security and persistence

All page handlers and service operations require active staff security administration; Razor Pages provides antiforgery protection. Search returns at most 21 results (20 displayed with a refine-search message), has a minimum query length, and uses escaped provider queries. No provider write endpoints are called. HTTPS is mandatory and HTTP redirects are disabled, including token requests. The directory secret is resolved on demand by reference through Common.Secrets; provider bodies and credentials are not shown in errors.

Selection tokens are encrypted/authenticated, expire after ten minutes, and bind the administrator, session version, provider configuration and immutable identity. Submission re-reads the selected identity from the directory and rejects disabled, deleted or changed identities. The browser cannot submit its own subject, provider, name or email. Contact email is directory profile information; it never authorizes account linking or claims email verification.

Common.Security owns account creation, identity uniqueness and the business-user projection. An atomic creation transaction checks the actor's current status/session version under a row lock, writes direct canonical roles and structured audit (actor, subject, time, new roles/status and verified binding). Any projection, role, authorization or audit failure rolls everything back. Revoked identity tombstones are respected. Existing accounts are returned without changing their lifecycle, roles, profile or bootstrap markers. Username conflicts with another binding require explicit investigation; email or name matches never merge accounts.

New pre-provisioned records have no last-sign-in timestamp and show “Never signed in.” First successful provider authentication resolves the existing immutable binding and records the actual sign-in. Pending accounts remain pending until approval; Active accounts receive only their assigned/direct and verified provider-group permissions. Suspensions, revocations, self-escalation and last-administrator protections continue through the existing administration workflow.

No database migration is needed: the existing Common.Security binding tables, nullable `AspNetUsers.LastSignInUtc`, roles and structured audit table suffice. Historical dates, account assignments and provider bindings are not rewritten.

## Validation and deployment

Run builds/tests with the pinned Common source manifest and disposable PostgreSQL. Do not point tests at production or enable `LiveFfpSql`. Validate administrator/reviewer/public isolation, provider identity-key matching, malicious/stale selections, duplicates, disabled identities, approval and first-sign-in reuse, atomic rollback and audit. Review Add Staff at desktop/mobile sizes.

Deploy only Request Portal with the existing Aegis.Operations tooling after PR checks pass. Preserve production environment/OpenBao configuration and data. Without the approved directory client the Add Staff page safely reports that provisioning is unavailable; deploying code does not enable directory access or add directory permissions. Verify an actual directory lookup and first sign-in in production only after its owner commissions that connection.
