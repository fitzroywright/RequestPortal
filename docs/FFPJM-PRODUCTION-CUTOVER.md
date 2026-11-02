# FFPJM production cutover

The bridge has two explicit modes: `Bridge:SqlTarget=Production` authorizes only
JMDB01/FFPJM; `Rehearsal` authorizes only JMDB01/FFP. Production appsettings selects
Production. Every SQL consumer passes this mode to the connection guard. Startup
validates the managed secret against the mode; SQL execution verifies the actual
machine, database, Kerberos authentication and (in Production mode) the dedicated
`FFTPJ\svc_ffpbridge` login. There is no automatic database fallback.

The existing deployment uses Production hosting with the rehearsal database.
Before deploying this candidate, explicitly coordinate its mode, SQL secret and
schema. Deploying the new default against the unchanged FFP secret fails closed.

## Preflight findings, November 2, 2026

A read-only connection from Kratos to JMDB01/FFPJM succeeded using the bridge's
existing service ticket cache and OpenBao/Common.Secrets provider. SQL reported
`FFTPJ\svc_ffpbridge` and `KERBEROS`. The managed connection remains JMDB01/FFP.
The login currently reports SQL Server sysadmin and effective database owner.
The sysadmin role is inherited through both `FFTPJ\Domain Users` and
`FFTPJ\Domain Admins`, so a service-only direct-role revocation is insufficient.
Changing these group grants affects other SQL users. This fails the least-privilege prerequisite; privilege correction requires
explicit authorization and dependency review, and was not performed.

The initial catalog inspection found existing business tables, but no
`ExternalRequestId` column, correlation unique index, integration outbox table or
either RequestPortal enqueue procedure. These objects require additive migrations.
Other inspected fields are Name varchar(400), ContactPerson nvarchar(255),
ReferenceNo nvarchar(500), Parish bigint and DocumentSource nvarchar(255), all
nullable. Existing first/last name fields need no migration: FirstName is nvarchar(200),
LastName nvarchar(100), both nullable. The bridge now uses these actual lengths and
rejects oversized values before opening SQL. It does not split organization names.
Name and ContactPerson retain their existing organization/applicant display mapping.

## Required migration plan

1. Run `003_FFPJM_ProductionPreflight.sql` read-only as the dedicated Kerberos
   identity. Capture full column metadata, indexes, defaults, constraints,
   module definitions and permissions before any change. Compare all existing
   objects with the candidate SQL. Missing objects are not proof of readiness.
2. Capture a current FFPJM full backup and verify the physical media with
   `RESTORE VERIFYONLY`; retain backup identity, time, media position, backup
   checksums and verification results. Where recovery currency requires logs or
   differentials, verify that complete chain. A prior FFP backup never qualifies.
   VERIFYONLY establishes media readability and backup completeness, not a
   demonstrated isolated database restore. Never restore over production.
3. Resolve the service's broad role membership through an authorized DBA change.
   Capture direct and inherited grants first. Its runtime needs SELECT on used
   lookups/business objects; INSERT/SELECT on new applications, items, documents
   and notes; appropriate UPDATE for bridge-owned synchronization fields,
   outbox delivery state and the shared XPO counter; and EXECUTE on enqueue
   procedures. It does not need sysadmin, db_owner, DDL or backup permissions.
   Apply DDL only through an explicitly authorized, audited migration procedure;
   the runtime identity must not retain elevated migration privileges. Do not substitute a personal administrator for
   the service's runtime or acceptance verification.
4. Only after reviewing the evidence, set the migration-session context key
   `FFPJM.VerifiedBackupEvidence` to the retained evidence reference, using
   `sys.sp_set_session_context`. This is an operator gate, not an automatic
   claim that a backup is valid. Run the SQL client with stop-on-error enabled.
5. Apply `100_FFPJM_ApplicationLetterExternalRequestId.sql` only if required:
   add nullable uniqueidentifier ExternalRequestId, then a filtered unique index.
   Reject incompatible existing definitions. No backfill or record updates.
6. Apply `101_FFPJM_RequestPortalIntegrationOutbox.sql` only if required:
   add the outbox with its constraints/index and create missing enqueue
   procedures. Existing procedure definitions are preserved; compare them
   separately and stop for an incompatible definition. No business tables are
   recreated or truncated. Never reset IDGeneratorTable.
7. Repeat preflight and directly verify schema and narrowly scoped service access.
   Confirm actual lookup values and XPO counter compatibility. Rehearsal scripts
   000/001/002 remain restricted to FFP and must not be repurposed for FFPJM.

## Activation and rollback gates

Prepare matching GitHub revisions, Linux payload and existing guarded deployment
backups first. The protected environment selects `Bridge__SqlTarget=Production`.
Use the existing authenticated OpenBao administrative procedure to update only
the existing SQL secret's database to FFPJM, retaining Integrated Security and
TLS. Never grant the workload write access to its own secret as a workaround.
The November 2 OpenBao administrative check found no operator session.

Activate only when binaries, production schema, least privilege, current backup
and rollback are all ready. Use Aegis.Operations guarded deployment entrypoints;
verify the checkout's GitHub origin and exact commit, rather than frozen local
origins. Preserve protected environment, service keytab/cache, secrets and NAS
configuration. Record the previous KV version and mode in protected rollback
state, and use the deployer's package backup if activation fails. Additive SQL
objects can remain after rollback; do not delete production application records.

Verify both services, trusted HTTPS health/readiness, actual SQL database and
Kerberos principal, TrueNAS paths, independent names/parish/reference, duplicate
submission behavior, outbox delivery and status reconciliation. Only then use a
new uniquely identified acceptance submission. Never replay FFP records
410861–410864. Record the production Application Letter ID and reference directly
from FFPJM. The candidate connection execution guard succeeded directly on FFPJM as the
service principal. Production preflight and migrations passed SQL Server
PARSEONLY validation; no DDL was executed. The full Release suite passed 18
domain and 125 infrastructure tests, with three explicitly skipped rehearsal
SQL tests because FFPMANAGER_TEST was unavailable. Linux bridge publishing and
guarded deployment smoke tests passed.

The missing transactional event hook in the separate FFP Manager
application must also be resolved before claiming full status synchronization.

No production migration, business write, secret activation or candidate deployment
has been performed while these prerequisites remain unresolved.
