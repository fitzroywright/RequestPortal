# Staff roles and details

The staff administration workflow uses the existing Common.Security authorization adapter with exactly two selectable application roles: Administrator and Reviewer. Canonical names live in RequestPortal.Common.Security.PortalRoles. Administrator retains its all-permissions grant; Reviewer retains staff access, request review and management dashboard access. Reviewer does not gain security administration.

## Data migration

EF migration `20261103160000_ConsolidateStaffRoles` creates structured staff administration audit storage and consolidates FfpAdministrator → Administrator and FfpReviewer → Reviewer. It reuses existing canonical role IDs, copies direct assignments and directory group bindings without duplicates, verifies each target binding, and only then removes obsolete bindings, claims and definitions. Group names and immutable provider identities are not renamed. Unrelated role definitions/assignments are retained outside this two-role workflow.

The operation is transactional and the SQL is safe to repeat. It locks the affected security tables, compares direct effective grants for active staff before/after, and requires equivalent grants for legacy directory mappings and any explicit legacy role policy. It does not union legacy permissions into shared canonical definitions, activate accounts, alter provider bindings, reset bootstrap state, or recreate users. Pending/disabled accounts remain in that state. Changed direct staff assignments invalidate existing staff sessions; a fresh provider sign-in retains the same account ID and effective permissions. Public account sessions are not changed.

If existing policy cannot be preserved safely, the migration fails and the transaction rolls back:

- RP-ROLE-001: duplicate canonical role definitions.
- RP-ROLE-002: incompatible legacy grants or an unproven directory mapping.
- RP-ROLE-003: legacy role claims differ from the target role.
- RP-ROLE-004 / RP-ROLE-006: assignment verification failed.
- RP-ROLE-005: active staff would gain or lose effective direct permissions.
- RP-ROLE-007: Reviewer already has administrative grants.

An active account with an otherwise inert legacy administrator role is not automatically granted Administrator permissions. Resolve any policy discrepancy through an explicitly authorized access decision, then retry. Do not bypass these checks or change grants merely to make the migration pass.

The migration is forward-only. Application-package rollback preserves the canonical assignments and audit table; it must not try to reconstruct stale legacy roles or discard subsequent access decisions. Normal deployment invokes the migration through the existing startup migrator. Initial administrator provisioning also applies the same verified role consolidation before checking for an existing administrator; its explicit verified identity, protected operator authority, one-time marker and repeat-execution safeguards remain in place.

## Staff details

Staff Information shows name, contact email, account status, authentication provider, registration date and last sign-in. Authentication identifiers are inside a closed Authentication Details disclosure. Access & Roles offers only Administrator and Reviewer, a read-only capability summary and closed technical permission details. Directory filters/role lists are shown on the directory page rather than duplicated below the details form.

Save Changes updates profile/role selections without a comment or routine confirmation. Pending staff remain pending after a save; Activate is the explicit approval action. Suspend and Revoke Access use browser confirmation dialogs and require server-side confirmation. Revocation removes the canonical staff roles; unrelated mappings are retained. An administrator cannot lose the last active direct Administrator, and a caller cannot self-grant additional permissions. Every operation still checks active staff identity and Common.Security authorization on the server, with version checks inside the atomic mutation.

The closed Audit History disclosure combines existing Common.Security events with structured events without deleting or rewriting old history. New events capture actor, subject, timestamp, action, previous/new roles and status; bootstrap reasons remain available. Actor display names are resolved where possible and timestamps are shown in Jamaica time. Existing audit text remains available in expanded details.

## Verification and operations

Migration tests cover repeated application, duplicate bindings, directory-group sign-in, permission equivalence, rollback for unsafe policies, legacy claim conflicts and pending-account preservation. Staff tests separately cover administrator/reviewer authorization, public account isolation, no-comment approval, destructive confirmations, self-escalation denial, last-administrator protection and structured audit.

Run relevant tests only against isolated PostgreSQL with `REQUESTPORTAL_POSTGRES` and `Category!=LiveFfpSql`. The historical application-number migration test now builds its own preceding schema directly instead of downgrading across a security data consolidation. Build and deploy with the exact Common library revisions in `security-baseline.json`.

Use the existing Aegis.Operations deployment script for Request Portal only, with a real isolated ProjectsRoot, compatible CommonRoot and a complete publish (not fast/no-build publish). Keep the external environment, OpenBao configuration, keys, storage and database intact. Verify startup, health and sign-in enforcement. If startup rejects a migration policy, retain the diagnostic code, use the established package rollback, and reconcile the policy before retrying.
