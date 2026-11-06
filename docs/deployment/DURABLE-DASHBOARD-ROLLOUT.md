# Durable Management Dashboard and input validation rollout

## Implementation and semantics

PR #82 staff routing remains intact: the default staff page and brand link use
`/Staff/Index` (canonical `/Staff`); explicit local returns and applicant flows
remain unchanged. Reports still contains Management Dashboard.

The 45-second cache is removed. The default dashboard reads one PostgreSQL row
from `management_dashboard_aggregate`, deserializes aggregate-only JSON and returns
it without querying historical requests or location storage. Scoped dashboards
and drilldowns retain the existing calculator. No chart, metric or map was removed.

The additive EF migration `20261106170310_ManagementDashboardAggregate` creates
one derived snapshot row, one trigger function and a pending-refresh outbox index.
Statement triggers cover requests/status history, Quick Intake, documents,
deliveries/returns, organizations, classifications and integration links. They
append a dedicated existing-outbox message in the business transaction, including
raw SQL updates. A transaction-derived message ID coalesces changes within the same
transaction; a worker coalesces all visible pending messages into one rebuild.
Geographic storage saves also enqueue refresh work after storing the location.
Since files and PostgreSQL do not share a transaction, periodic reconciliation
covers an interrupted location save between its storage write and queue commit.

The background worker polls every two seconds. A PostgreSQL transaction advisory
lock coordinates instances; repeatable-read isolation covers source queries,
publication and acknowledgement. Events committed during a build remain pending.
A crash/cancellation releases the lock and rolls back publication/acknowledgement.
Retries use bounded exponential backoff up to five minutes; failed JSON builds
never replace valid JSON. Storage outages fail aggregate builds rather than
publishing fallback geography over the previous valid snapshot. Existing legacy
missing/corrupt-location fallback behavior remains in the calculator.

A full scheduled rebuild runs at least every minute when healthy; reconciliation
is recorded at least every fifteen minutes. Date-based metrics retain their
existing UTC semantics. Publication is atomic. SnapshotVersion increments only
on successful builds. GeneratedUtc identifies computation start; LastUpdatedUtc
and LastReconciledUtc are persisted. The existing refresh display identifies the
snapshot timestamp and warns if refresh failed or the data is older than two
minutes. Retry delays or outages can increase staleness; this is shown, not hidden.

Startup applies the migration and initializes/verifies a snapshot before the HTTP
server starts. A valid previous snapshot survives refresh failure. First startup
fails closed after 90 seconds if no verified snapshot can be built, allowing the
existing deployment health/rollback procedure to restore application binaries.

## Input validation

Weekly Income (JMD) uses number/min/step and mobile decimal controls. Both normal
and upload-assisted submission validate the raw invariant input: zero or a
nonzero-leading integer, optional one/two decimal digits, no sign, exponent,
grouping or other symbols. Decimal bounds prevent numeric(14,2) monthly overflow.
The persisted monthly contract remains weekly × 52 ÷ 12, rounded to two decimal
places with the existing ToEven rule. Valid attempted values survive other errors.
No stored income is rewritten; the read-only quality report separates legacy
unknown income sentinels from other negative values.

New telephone input reuses libphonenumber-csharp 9.0.42 via ApplicantTelephone.
IsValidNumber supplies numbering-plan validation; Jamaica 876/658 is the default
for national input, and explicit international prefixes retain their country code.
The existing legacy Normalize method and Bridge contract remain unchanged for
queued/historical transfers. Canonical storage uses E.164 within the existing
15-character integration boundary. Both optional numbers are normalized. Existing
business behavior permits equivalent contacts; the existing Bridge de-duplicates
them, and no unrelated distinct-contact rule is introduced. Display formatting
uses the same library. No carrier is inferred and no historical number is migrated.

ContactEmail validates bare address structure, rejects controls before trimming,
retains local-part case and legitimate plus characters, normalizes domains with
IDN handling, and enforces field/local/domain bounds. Profile, organization,
Quick Intake, invitation and notification entry use the shared mechanism.
Authentication/OAuth subjects and account links are untouched; invitation
comparisons remain case-insensitive for existing stored invitations. Razor encodes
stored display values; mailto links escape the whole validated address. Outgoing
email validates recipient and rejects subject controls. Values are never joined
into SQL text. Shared telephone client rules, native input types and existing
inline validation/layout are retained without introducing cards or redesigns.

Reference: [libphonenumber API](https://github.com/twcclegg/libphonenumber-csharp),
[MailAddress.TryCreate](https://learn.microsoft.com/en-us/dotnet/api/system.net.mail.mailaddress.trycreate).

## Validation and measurements

The Release regression suite passed 19 domain and 607 infrastructure tests, zero
failures; ten opt-in live SQL tests skipped because no SQL test target was supplied.
After that run, an additional storage-outage preservation safeguard passed the
19 focused aggregate/calculator tests. The final input suite passed 37 tests,
including both submission handlers, overflow, preserved attempted values and a
read-only historical-count audit. No Docker or production database was used locally.

A controlled benchmark uses 1,344 synthetic requests, isolated local PostgreSQL
and simulated 2 ms metadata/read latency. The final storage-focused run measured:

| Service path | Duration |
| --- | ---: |
| Historical calculator baseline | 681.03 ms |
| PostgreSQL snapshot, first read | 21.92 ms |
| PostgreSQL snapshot, repeat read | 7.25 ms |

Snapshot reads performed zero location-storage reads. These measure service calls,
not production browser load times; the PR #82 memory-hit timing is not a database
read baseline. Production timings and the truncated Part 9 performance targets
remain unverified. Use the same authorized staff session and browser conditions
for production before/after measurements; a 302 challenge is not dashboard timing.

## Production gates — not executed by this document

1. Confirm the PostgreSQL endpoint/database, migration history, schema ownership,
   trigger/function privileges and outbox insert permission under the existing
   application identity using the companion read-only preflight. Never substitute
   another database. Retain a current full backup and verified restore evidence
   for that exact database before migration. Do not log connection strings or secrets.
2. Run the reviewed read-only `RequestPortal.IntegrationAudit input-validation`
   mode in the existing service configuration under the application identity.
   It checks axios.fftpj.local/requestportal, starts a read-only transaction and
   emits per-field counts without contact values. It does not migrate, start the
   web application, send notifications, process queues or change identity links.
   Installing/replacing a hash-pinned audit bundle requires its approved access
   procedure; deployment/install privileges must not bypass that boundary.
3. Keep the existing root-owned environment and OpenBao configuration. Build a
   clean GitHub main using the existing Aegis.Operations Request Portal-only
   procedure; do not invoke the integrated Bridge deployer. Retain source commit,
   Linux artifact hash and application backup path.
4. Deploy only requestportal using the matching installed deployer, with the
   existing rollback behavior. The application starts the migration and verified
   initialization before serving the new read path. Verify trusted public health,
   active/running systemd state, source artifact hash and initialization logs.
5. Under an approved read-only DB connection, verify one snapshot row with
   SnapshotVersion > 0, object-shaped JSON, Current status, recent timestamps,
   and totals that reconcile. Inspect refresh messages and worker errors. Observe
   a scheduled version advance without changing applicant data.
6. With approved staff authentication, verify Requests default navigation,
   explicit authorized returns, applicant navigation, every dashboard section,
   and measured cold/warm browser loading. Observe existing ordinary activity
   updating the snapshot without duplicate notifications or synchronization loss.
7. If health/startup fails, use the existing application rollback. Retain additive
   schema and all business records. The Down migration is tested on an isolated
   database and removes only the new triggers/function/index/derived snapshot;
   never run destructive business-data rollback or reset production.

At preparation time Kratos has no pg_dump/pg_restore. PostgreSQL backup evidence
is unavailable through the approved hash-pinned audit. SSH to Axios fails normal
host-key verification; trust was not weakened. No production migration, snapshot
initialization, historical-count scan or deployment has been performed for this
candidate. These are actual rollout blockers, not successful checks.

## Optional historical remediation — preview only

The audit reports inspected, empty, invalid-under-new-rules and valid-but-not-
canonical counts per field. An empty value or legacy unknown sentinel is not a
confirmed invalid answer. Production counts are unavailable until the approved
reader runs; no zero counts are claimed.

A later explicit remediation should preview immutable record ID, field, protected
old value, proposed canonical value and reason. Never guess missing telephone
digits, change country/carrier, reinterpret a legacy income sentinel, or change
email ownership/identity links. Ambiguous values require applicant/administrator
confirmation. Apply only individually reviewed changes with old-value/concurrency
comparisons, bounded transactions and audit evidence. Preserve independently
edited values and all original records. No bulk cleanup is part of this rollout.
