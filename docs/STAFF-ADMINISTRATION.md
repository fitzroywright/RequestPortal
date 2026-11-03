# Staff administration and initial provisioning

Staff authenticate through the existing FFP OIDC provider. Common.Security resolves the immutable provider/scope/subject in `RequestPortal.Staff`, creates the existing business-user projection, and leaves new staff Pending Access. Public applicants and Organization Managers use `RequestPortal.Public`; matching email addresses never convert or link these populations.

The existing RequestPortal authorization adapter owns `AspNetRoles`, `AspNetUserRoles`, directory group mappings and role permissions. Its `Administrator` role grants Common.Security's explicit `*` permission. This includes `requestportal.access`, `requests.review`, `ManagementDashboard.View`, and `security.manage`. No alternate role model is introduced.

## Provision the first administrator

Use an authorized host operator with access to the existing production service environment and OpenBao credentials. Database access is administrative authority; the authority argument identifies the approved operator/change record for audit, and is not a password or a substitute for protected credentials. Do not grant general web users or the SSH deployment account access to production secrets to run this command.

Obtain the account ID and exact provider ID, scope ID and subject ID from the existing staff account's immutable identity binding. An authorized database operator can inspect only Request Portal PostgreSQL:

```sql
SELECT a.account_id, a.login_name, a.display_name, a.status,
       i.provider_id, i.scope_id, i.subject_id
FROM common_security_accounts a
JOIN common_security_identities i USING(application_id, account_id)
JOIN "AspNetUsers" u ON u."Id"::text=a.account_id AND u."IsInternalStaff"
WHERE a.application_id='RequestPortal.Staff' AND a.population=0;
```

Verify the intended person's directory identity against the approved administrative request. A sole pending account, display name, email match or first sign-in does not establish approval. Never guess the target or bind by email. Account statuses: 0 PendingAccess, 1 Active, 2 Suspended, 3 Disabled.

Run from the deployed application's working directory, as the service identity, using its existing protected environment loaded by the host's approved service-management procedure:

```sh
dotnet RequestPortal.Web.dll provision-staff \
  ACCOUNT_ID PROVIDER_ID SCOPE_ID SUBJECT_ID \
  'approved-operator/change-reference' 'Reason for first administrator approval'
```

The application resolves its PostgreSQL connection through the normal Common.Secrets/OpenBao configuration. No web server is started and no business migrations or downstream FFP integration are executed by this command. All six arguments are mandatory. No credentials belong in command arguments. A privileged operator must supply the existing protected service environment; running as the SSH deploy user alone is insufficient.

For an approved offline maintenance session, the separately built migration tool also supports:

```sh
dotnet RequestPortal.SecurityMigration.dll bootstrap-staff \
  ACCOUNT_ID PROVIDER_ID SCOPE_ID SUBJECT_ID \
  'approved-operator/change-reference' 'Reason for first administrator approval'
```

This tool reads only the protected `REQUESTPORTAL_MIGRATION_POSTGRES` environment value. Use the Request Portal PostgreSQL database exclusively. Do not use FFP or FFPJM SQL Server connections.

Provisioning checks the existing staff population and exact external binding, rejects local/demo identities, assigns the existing Administrator role, activates the account, and records a permanent `requestportal_staff_provisioning` marker. Roles, activation, marker and administrative audit commit in the Common.Security `ChangeAuthorization` transaction, which invalidates previous sessions. There are no new accounts or provider bindings. Persistence and effective `security.manage` are checked afterward. Repeat execution for the same active administrator does nothing; another target is rejected. Repeating after a later suspension/revocation never restores access. Existing active administrators also block bootstrap in an installation without a marker. Use ordinary administration for subsequent approvals and access recovery through an authorized administrator.

Sign in again as the approved identity. Confirm Administration → Staff & Roles, inspect the Administrator role and all effective permissions, and provision a second approved administrator through the normal pending approval workflow.

## Normal administration

The staff directory supports text search, status and role filters, and name/username/email/provider/status/sign-in/registration sorting. Pending Access shows the outstanding count. Select staff to inspect immutable identity bindings, update name/contact email, choose roles and review effective permissions. Save Changes updates details and roles; Activate explicitly approves pending access. Only Suspend and Revoke Access require confirmation dialogs. Ordinary saves and activation require no free-text comment or routine checkbox. Contact edits do not change authentication bindings.

Role permissions come directly from the Common.Security authorization adapter. Only Administrator and Reviewer are selectable. Legacy staff roles are consolidated by the verified data migration; unrelated mappings are preserved outside this workflow. Role links filter the directory to direct role holders. Effective permissions include the latest observed, verified directory groups, while the editable assigned-role list contains direct assignments. Provider group changes take effect on fresh authentication; group observations are for display, never trusted as administrative authority.

The former generic account mutation API is replaced by the confirmed staff workflow so direct status/identity mutations cannot bypass last-administrator protection. Its staff directory GET remains available under the same staff administration policy.

All reads and mutations require the staff authentication scheme and server-side access/security permissions. The service rechecks current account state and Common.Security authorization. Mutations check account/session versions inside a serialized transaction. Concurrent stale changes are rejected. The final active, directly assigned Administrator cannot lose that role or be suspended/revoked; retain explicit direct administrators even when directory groups grant administration. Public Organization Managers cannot administer or be selected as staff.

Status, role and profile decisions retain the Common.Security account history and add structured actor, subject, previous/new roles, status and time records. Bootstrap reasons remain recorded; routine decisions need no comment. Verified staff sign-ins record last sign-in and access events. Role/status changes invalidate staff sessions; public authentication handlers remain unchanged. Audit history displays the latest 200 events.

## Schema, deployment and verification

Two additive PostgreSQL tables are initialized idempotently with the existing security schema conventions: `requestportal_staff_provisioning` and `requestportal_staff_signins`. EF business schema and existing mappings are retained. Do not clear security tables, marker, staff users or mappings when resetting demo business data. Application startup warns `RP-STAFF-BOOTSTRAP-001` when no directly assigned active staff Administrator exists; it never automatically promotes a user. Keep security data and the marker in database backups.

Build against the exact shared-library revisions in `security-baseline.json`. Run Debug and Release tests using an isolated local PostgreSQL database and `--filter 'Category!=LiveFfpSql'`, matching the coordinated security workflow. SQL Server tests are outside this task and must not be enabled. Deploy only Request Portal through Aegis.Operations `scripts/Deploy-Kratos.ps1 -Applications requestportal` using an isolated source root and pinned CommonRoot. Keep production environment, OpenBao configuration, PostgreSQL data, storage, key ring and registration identity intact.

After deployment, check systemd startup, `/health` with `Host: requestportal.ffpja.org`, unauthenticated rejection of `/Administration/Staff`, public sign-in entry points, and an actual approved staff session. Complete live provider callbacks and authorized UI acceptance with the relevant identity owner; route availability alone does not prove successful provider authentication.

The current details layout, role-consolidation safety checks and migration procedure are documented in [STAFF-ROLE-CONSOLIDATION.md](STAFF-ROLE-CONSOLIDATION.md).

## Add staff before first sign-in

Authorized administrators can use Staff & Roles → Add Staff once the approved read-only directory connection is commissioned. Search the configured provider, review the verified identity, choose canonical roles and Pending Access or Approve & Activate. Existing identities are never duplicated or silently reactivated. See [staff pre-provisioning](STAFF-PREPROVISIONING.md) for provider-specific identity matching, required read-only permissions, secret references and verification.
