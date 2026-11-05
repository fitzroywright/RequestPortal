# Request Portal → FFP Manager integration monitoring

The production destination is **JMDB01 / FFPJM**. Request Portal retains its existing PostgreSQL database. The monitoring migration does not change SQL Server, source requests, intake answers, ownership, documents, credentials or connection targets.

## Submission and evidence

Quick Intake saves the request, intake, integration link and original submission event in one PostgreSQL transaction. The existing outbox projects a deterministic FFP Manager submission message. `RequestId` and an indexed `NextAttemptUtc` allow bounded database queries without starvation behind delayed messages. Terminal validation failures retain their actual attempt count; they do not pretend to have exhausted ten attempts. A terminal message also carries a far-future compatibility tombstone in the legacy encoded schedule so an older worker cannot automatically replay it after binary rollback. This is a no-retry guard, not a scheduled retry; the monitoring page shows no next retry.

Each outbound attempt persists a start checkpoint before preparing the payload. Stages include preparation, destination lookup, SQL submission, acknowledgement verification, artifacts and confirmation. A process termination leaves an incomplete attempt; a pending checkpoint older than five minutes is shown as **Reconciliation Required**. Missing links/queue records and overdue retries are also discoverable without creating recovery work automatically.

Persisted status values remain compatible with the previous release (NotSent/Pending/Synchronized/Failed); Retrying and Reconciliation Required are derived from durable scheduling, classification and verification metadata. The existing integration link is the correlation ID. It stores the destination application ID/reference, verified target identity/time, attempt count, next retry, failure stage/classification/code and initial successful synchronization time. Append-only `integration_attempts` retain failed, successful and interrupted outcomes after retries. Historical failures that predate this infrastructure cannot be reconstructed into complete attempt histories. Before clearing a legacy error, recovery preserves a fingerprint and the known recorded timestamp as a clearly labelled historical snapshot; it does not invent attempt numbers or copy unsafe exception payloads.

Before an insert or retry, the portal calls the authenticated bridge lookup by immutable **PortalRequestId / ExternalRequestId**. An explicit 204 response means no destination application was found, including archived matches. An unavailable/old lookup route, authentication failure, conflicting identity, missing destination reference or duplicate destination matches blocks the transfer. A normal HTTP success is insufficient: the returned application ID/reference must match a fresh destination lookup on JMDB01/FFPJM. The SQL gateways retain their serializable, immutable-ID deduplication.

The verified SQL identity is saved before transferring artifacts. A document failure therefore preserves the committed application ID. Subsequent safe retries use the existing application and idempotent document IDs rather than creating a second application. Applicant/staff confirmation events are released only after verification and artifact completion.

Transient connection/time-out failures use bounded exponential retry delays (1, 2, 4, 8, 16, 32, then at most 64 minutes), with ten outbound attempts before operator intervention. Permanent mapping, validation, authentication and SQL constraint failures stop automatic retry. Exceptions sent to staff/exported diagnostics contain fixed sanitized classifications, not remote response bodies, connection strings, OAuth data or credentials.

Return-event callbacks are monitored separately. The authenticated bridge exposes aggregate SQL outbox counts. The existing PostgreSQL business audit store records changes to callback health, including unavailability and recovery, without clearing earlier observations or adding a SQL Server history table. A failed callback is not labelled an initial-submission failure.

## Staff interface and permissions

**Administration → Staff & Roles → Integrations** opens `/Staff/Integrations`. Existing staff-surface and `requestportal.access` authorization applies to the page and exports. Monitoring provides server-side search (applicant, portal reference, FFP Manager reference), status/date filters, deterministic sorting/pagination, source review links, sanitized failure/attempt history, recorded callback health and CSV diagnostics. CSV cells are quoted and protected against spreadsheet formula injection.

Request/Quick Intake staff review shows transfer status and a diagnostic link. The staff dashboard's Needs Attention area includes failed/reconciliation-required transfers and failed/unavailable return-event monitoring. Applicant/public pages do not receive these technical diagnostics.

Retry, reconciliation, dead-letter replay and the existing SharePoint write test require administrator `security.manage` permission. Integration recovery additionally verifies the authenticated staff surface, access permission and exact actor ID inside the service. Razor antiforgery protection remains required. Generic dead-letter replay cannot bypass the dedicated FFP Manager reconciliation/retry path.

Reconciliation reads FFP Manager and updates only portal tracking/audit data. Retry may create an absent destination application or resume artifacts. Operators must review the recovery recommendation and correct permanent source/mapping problems first. There is no bulk replay button or migration-driven recovery. Every manual request/outcome is audited. Attempt counts describe outbound submission attempts; reconciliation/status refreshes have separate operation records. The page shows the latest 100 outcomes; the complete attempt history can be exported without loading it all into the browser.

## Read-only production reconciliation

`tools/RequestPortal.IntegrationAudit` is an offline console tool, not website startup. It does not start workers, run migrations, repair destination references, probe storage writes or modify application records. Its launcher runs the PostgreSQL and SQL queries under the existing service identities, using the active services' protected configuration and Common.Secrets. It explicitly disables consumption/deletion of bootstrap SecretId files. The PostgreSQL snapshot is transactionally read-only; the SQL statements are fixed SELECTs guarded by Production/JMDB01/FFPJM/Kerberos validation.

The selected evidence includes source references, link/outbox metadata, active/archived destination identity matches and callback outbox counts. Journal output contains approved exception types, SQL codes, known mapping-field names, explicitly labelled business correlation IDs and fingerprints; raw messages, credential-shaped GUIDs, applicant payloads and secrets are omitted. Evidence belongs in a private operator file, never in GitHub.

On 2026-11-04, service status and the established bridge commissioning inspection showed both services active, successful HTTPS health/readiness and a bridge deployment manifest targeting Production/JMDB01/FFPJM. Direct journals/configuration and database reconciliation were unavailable to the ordinary SSH identity. This is an access limitation, **not evidence that rows or credentials are missing**. No production row counts or individual failure causes were established by that preflight.

A root-owned audit bundle was staged at `/var/tmp/requestportal-integration-audit-20261104`. The fixed command requires an authorized Kratos administrator's sudo authentication:

```sh
umask 077
sudo /usr/bin/python3 /var/tmp/requestportal-integration-audit-20261104/run-readonly.py > ~/requestportal-integration-reconciliation.json
```

The resulting report differentiates verified synchronization, committed-but-unacknowledged applications, absent queue events, synchronized links without a target and conflicting/duplicate identities. Recommendations are proposals for a reviewed recovery plan, not authorization to perform recovery writes. A missing active match may also reflect archival/deletion; investigate that before resubmission. Field/error-pattern classifications identify evidence and must not be presented as a fully confirmed root cause without corroboration.

## Migration and rollout gates

`IntegrationTransferDiagnostics` adds nullable metadata, an attempt table with a restrictive foreign key, terminal/scheduling metadata and indexes. Its only backfills correlate existing JSON outbox RequestIds and preserve existing encoded retry schedules. It does not enqueue/replay transfers or reset outcomes/attempt budgets. Lock and statement timeouts bound migration execution. Rollback retains the additive schema and audit history; it must not run a destructive down migration.

Before production schema changes, record the actual PostgreSQL target and verify a current backup/PITR reference, retention and restore procedure. Review the generated SQL and rehearse the upgrade against disposable PostgreSQL. An application/configuration backup from the deployer is not proof of a database backup. The previously merged identity-recovery migration is also pending on the older Kratos portal release and must be included in that review.

After CI and review gates, synchronize the deployment checkout with verified GitHub main. Use Aegis.Operations' existing bridge deployment tool first (additive authenticated lookup/diagnostic endpoints), preserving its Production configuration and checking readiness. Validate lookup using the approved protected credential before publishing the portal. Use `Deploy-Kratos.ps1 -Applications requestportal` for the portal-only rollout. Preserve protected environment files, OpenBao configuration, PostgreSQL data, storage, Data Protection keys and existing service identities. Do not deploy Studio or change FFPJM schema.

Verify deployed commit/artifact hashes, both service states, HTTPS health, authenticated diagnostic/review pages, read-only destination lookup, callback health and sanitized logs. Do not create production applications merely to demonstrate monitoring. Controlled synthetic scenarios belong in disposable tests; any production recovery requires the reconciled, reviewed plan.


## Telephone validation and source preservation

Quick Intake and the bridge share the same libphonenumber-based validator. Jamaican local numbers and explicit international numbers normalize to E.164. Bare `00` and `011` international access prefixes are accepted; a malformed existing `+011` value is rejected rather than guessed or rewritten. Extensions, unsupported characters, impossible lengths and the SQL column's 15-character limit are checked before a new intake commits or a transfer inserts data.

`OriginalTelephone` and `OriginalSecondaryTelephone` retain the exact submitted strings for new intakes. Historical rows remain null: previously discarded formatting cannot be recovered by inventing a value. These fields are private persistence metadata and are not added to public responses, notifications or diagnostics exports. Existing canonical phone, identity and notification fields retain their roles.

Bridge failures identify `VALIDATION_PRIMARY_TELEPHONE` or `VALIDATION_SECONDARY_TELEPHONE` without including the number. Existing durable diagnostics retain the request reference, attempt time, retry and resolution history. These are permanent validation failures; existing destination bindings must be reconciled before any authorized retry. This release does not bulk correct or replay production records.

The additive `PreserveSubmittedIntakeTelephones` migration must follow verified production PostgreSQL backup and isolated restore testing. It adds two nullable bounded columns with short lock/statement timeouts and no data rewrite or automatic recovery. Application rollback retains these columns and all captured originals; destructive migration rollback is deliberately disabled.


## Restricted exception evidence (2026-11-05)

New portal and bridge exceptions are classified for normal staff, while approved and bounded diagnostic information is attached to the latest durable integration attempt in PostgreSQL `integration_attempts.ExceptionDetails` (nullable, max 4000 characters). The migration is `20261105070000_IntegrationExceptionDetails`.

The normal integration list, ordinary diagnostics endpoint and transfer status service do not include `ExceptionDetails`. The separate GET `/Staff/Integrations?handler=Exception&requestId=...` verifies the existing `ManageSecurity` permission, returns no-store plain text and is loaded only when a security administrator expands Full Diagnostics. The browser inserts it as text, never HTML. History queries always use the *latest attempt* even if older attempts have diagnostics, avoiding a misleading stale exception.

Restricted evidence contains exception and inner-exception **types**, allowlisted exact validation messages, stage, classified error code, method-name-only stack frames and (on SQL failures) SQL error number, sanitized stored procedure identifier and line number. No raw exception body, SQL statement, parameter values, source file path, personal details, connection string or arbitrary exception message is persisted for browser display. Complete server-side exception logs remain subject to separate OS/log permissions and retention. Historical failures without original telemetry cannot be retroactively filled in.

**Deployment prerequisite:** Back up and verify the live Request Portal PostgreSQL database. Apply/review the additive migration before running new application code; new binaries write `ExceptionDetails` on failed attempts. The change does not alter JMDB01/FFPJM schema or send/retry any requests. Build and run the infrastructure tests, migration parity tests and authorized/unauthorized page tests. Verify both Request Portal and Bridge binaries understand the new optional `TransferFailure.TechnicalDetails` response, and verify the privileged endpoint does not leak to unprivileged users. Do not deploy the Bridge implicitly as part of a simple Portal UI release; coordinate the specific rollout separately.
