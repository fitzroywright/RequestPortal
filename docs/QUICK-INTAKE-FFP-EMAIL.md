# FFP Manager references and Quick Intake notifications

The integration keeps `IntegrationLink.ExternalReference` (ApplicationLetters.ID)
separate from `IntegrationLink.ApplicationNumber` (the generated ReferenceNo).
The nullable PostgreSQL column is added by
`20261014190000_AddFfpManagerApplicationNumber`. Existing links are not backfilled
and historical applications are not replayed.

When integration is enabled, a submitted request queues its downstream transfer.
The bridge must return a nonblank FFP reference distinct from the Portal code.
After supporting documents transfer successfully, the Portal stores both external
identifiers and queues one durable applicant confirmation per request. Failed or
invalid transfers remain retryable without releasing reference notifications.
A retry reuses the external application and notification identities.

The applicant confirmation labels the generated FFP reference and Portal code
separately. Existing email preferences and WhatsApp consent/commissioning still
apply: WhatsApp being disabled does not block an available email channel. In-app
messages and external delivery identities remain stable across retries. When the
bridge is disabled, the existing Portal-code submission confirmation is retained.

Quick Intake's dedicated Client Relations email remains a separate durable event,
created transactionally with the intake. It waits for the FFP reference without
consuming delivery retries, then uses `Notifications:QuickIntakeClientRelationsEmail`.
Its message contains both references and asks staff to review the Portal. A failed
staff email does not rerun the bridge or block the applicant's notification, and
regular requests do not acquire a Quick Intake staff email. The older proposed
`Notifications:FfpEmailAddress` and generic staff-email event are superseded.

Common.Messaging owns transport retries and provider idempotency. Completion of
the Portal outbox means durable queue admission, not confirmed provider delivery.

Quick Intake keeps its explicit first/last names, address, email, gender and
employment status. Regular organization requests keep organization fields and do
not infer applicant fields from organization names. The bridge's Jamaica business
time conversion and reference-year boundary behavior are unchanged.

Production SQL guards continue to authorize only JMDB01/FFPJM in Production mode;
Rehearsal authorizes only JMDB01/FFP. See `FFPJM-PRODUCTION-CUTOVER.md` for the
production migration/activation plan. Old local rehearsal notes are historical,
not instructions to switch an active production deployment back to FFP.

Regression tests use isolated PostgreSQL. Optional `FFPMANAGER_TEST` tests use the
shared SQL connection guard in explicit Rehearsal mode, reject FFPJM before any
connection, and report a skip when unavailable. They must never be pointed at
production; their synthetic fixture cleanup is limited to the generated request
identity, and the counter-only test rolls back without rewinding the counter.
A skipped SQL test is not live database acceptance evidence.
