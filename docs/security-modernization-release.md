# Security modernization candidate — deployment gate closed

This is a coordinated source candidate, not an approved production rollout. `security-baseline.json` pins every shared library; no application may substitute a floating Common.Security checkout or overwrite shared running binaries. Build in an isolated output directory and deploy a complete application bundle independently.

## Build and evidence

Run `python3 ../Common/Common.Security/scripts/verify-source-baseline.py security-baseline.json --common-root ../Common` before building. Run the existing application tests and the coordinated security workflow. Failed, skipped, missing or empty results block release. The workflow uses disposable PostgreSQL 16 and cannot replace live provider or operational acceptance.

## Data and identity preparation

1. Capture the currently deployed application package, protected configuration, database backup, key rings, plugin versions and credential-store references. Verify a restore into an isolated environment before any production write.
2. Review an owner-only approved account manifest. Preserve each existing account ID and login alias. Resolve immutable AD domain/user GUIDs or Entra tenant/object IDs through an authorized directory administrator. Email and usernames do not authorize bindings. No production identity import has been executed by this change.
3. Rehearse the additive common schema and approved imports on a representative restored database. Verify account counts, existing business references, role assignments and security audit. New staff/operators must remain Pending Access until explicitly approved.
4. Keep at least two approved administrators. Test login, disabling, linking/unlinking, role removal and session revocation before enabling broader access. All pre-cutover human cookies require a fresh login to acquire a verified account/version binding.

## Rollout and recovery

Stage the complete candidate alongside the last known-good package. Keep each application's database and deployment independent. Run its acceptance matrix, verify protected configuration and backups, then cut over only that application. No deployment is authorized by a successful shared-library build alone.

On a stop condition, stop the affected rollout and return traffic to the previous package. Preserve new business records and the additive security tables/audit. Do not restore a stale pre-cutover database after new writes; reconcile identity changes forward or recover current business writes into a restored copy before an explicitly approved restore. Do not drop old identity history or business tables. Re-run smoke and security checks after recovery.

## Required operational acceptance

Public Microsoft, Google, Yahoo, Facebook and Apple handler registrations and callback paths are preserved. Common.Security resolves the provider's verified immutable subject after the protected callback; public applicants become Active immediately. Staff use the distinct `RequestPortal.Staff` account namespace and cannot convert an applicant through a matching email. Staff authorization uses the original immutable business-user ID and is rebuilt on cookie validation. Pending staff receive no Access permission.

Run `dotnet run --project RequestPortal.SecurityMigration -- export-public protected-public.json` with `REQUESTPORTAL_MIGRATION_POSTGRES` pointing to the restored test database. This reads historical AspNetUserLogins without changing them. Review the manifest, resolve staff records carrying social links explicitly, protect it (owner-only), then use `apply-public`. Use `apply-staff` only with directory-approved stable bindings and original staff IDs. Both imports are transactional and preserve business profiles, associations, roles and historical provider rows. The old provider-history table is retained for migration/audit and is not a runtime authentication fallback. Additional public provider links require a fresh signed-in account plus a correlated provider callback through Profile → Manage sign-in methods. Email matches alone never link.

Staff OIDC retains `/signin-ffp-staff`. Commission `Authentication:StaffOidc:Issuer`, `ProviderId` and `AllowedTenantIds` for Entra, with Common.Secrets supplying the client secret. A federated AD OIDC issuer uses its immutable subject. The existing external reviewer proof remains narrowly scoped to the business review invitation.

Mandatory live gates: existing Microsoft/Google/Yahoo sign-in and callbacks; existing applicant associations; new applicant registration; pending and approved staff; Quick Intake form, persistence and reference generation; FFP Manager bridge duplicate/idempotent submissions; acknowledgments/notifications; public URLs, HTTPS/TLS and cookie behavior. PostgreSQL test success alone does not establish those live-provider/integration results. Do not deploy while any gate is absent or failing.
