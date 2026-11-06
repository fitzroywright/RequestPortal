# Requests grid and household mapping deployment gate

## Candidate and observed production state — 6 November 2026

The candidate reuses PR #78's current Date Submitted implementation: persisted
`AssistanceRequest.SubmittedUtc`, six columns, `dd MMM yyyy`, em dash for null,
no Age column or age calculation. Existing queue ordering, filters, pagination
and row destinations remain in place. Rendered-page and PostgreSQL projection
regressions cover the behavior.

The approved production audit at 15:57 UTC reports JMDB01/FFPJM and enabled Portal
integration. Both existing systemd services are active/running; trusted HTTPS
Portal health, Bridge health and Bridge readiness passed. These describe the
existing deployment, not this candidate. No candidate deployment or acceptance
submission has run.

Kratos currently grants passwordless execution only to the existing hash-pinned
integration audit and deployment/install commands. Broader sudo requires a
password. The approved audit does not query field definitions, field values or
backup evidence. Its code and grant were preserved. Do not use installation or
deployment privileges to bypass that audited access boundary.

Run `FfpManager.Bridge/Sql/004_FFPJM_HouseholdMappingPreflight.sql` through an
approved SQL reader under the existing dedicated Kerberos service identity.
Retain complete result sets, including defaults, checks, foreign keys and
triggers, and inspect any referenced lookup definitions. A DBA may need to
provide backup-catalog and retained backup verification evidence separately.
Metadata visibility must be sufficient; missing/inaccessible definitions cannot
be accepted as absence of constraints. The actual Dependents definition and
current verified production backup are not yet established by this mission.
No schema changes or production data writes are authorized by this script.

## Transfer findings and pending implementation

`FfpManagerIntegrationService` already takes Quick Intake EmploymentStatus into
`ExternalApplicationSubmission.ApplicantEmploymentStatus`. `FfpManagerBridgeClient`
serializes that contract with JsonContent; the Bridge endpoint consumes it.
Both SqlFfpManagerGateway and SyncedSqlFfpManagerGateway call the shared
BridgeApplicationReceipt parameter mapper, which binds Occupation as
nvarchar(255), rejects values over its limit and does not substitute a job title.
Actual FFPJM field compatibility and stored values still need direct verification.

Children Under 18 is currently absent from the transfer contract and INSERTs.
Implementation is held at the user's explicit schema-before-implementation gate.
Once compatibility is established, append an optional nullable boolean to the
contract so missing fields in old JSON remain unknown. Map true to One or More,
false to None, and null to SQL NULL only if the verified destination supports
those values. For Quick Intake use the existing paired legacy sentinel
`DateOfBirth == 1900-01-01 && StreetAddress == "Not captured"` to retain unknown;
never treat that backfilled false as a confirmed No or derive a numeric count.
Deploy the receiving Bridge before the sending Portal. Preserve old payloads,
queue processing, authentication, attachments and reverse-status synchronization.

## Existing-record reconciliation scope — preview only

The approved audit inspected 1,344 Quick Intakes:

| Identity/transfer result | Count |
| --- | ---: |
| Verified synchronized by immutable request ID and destination reference | 1,247 |
| Destination committed but Portal acknowledgement needs review | 33 |
| Never queued, destination absent | 20 |
| Other records without a verified destination | 44 |

The 1,247 verified synchronized records are the initial field-review cohort,
not a count of fields needing correction. Another 33 require acknowledgement
review before field repair. The 64 absent records are a separate transfer-recovery
concern; never insert them as part of an existing-record field correction.
No Occupation/Dependents mismatch count is available from the approved audit.
Detailed local identity evidence is retained outside Git in
`~/Projects/requestportal-mission-audit.jsonlog` and
`~/Projects/requestportal-mission-preview.json`.

Controlled reconciliation after schema verification:

1. Read a consistent source snapshot and current destination values under the
   approved identities. Match `integration_links.RequestId` and
   `ApplicationLetters.ExternalRequestId`, then verify stored destination ID,
   independent ApplicationNumber/ReferenceNo, active lifecycle, and uniqueness.
   Never match solely on applicant name, telephone or public reference.
2. Export a protected per-field preview: immutable identities, original intake
   answer/legacy classification, expected mapping, current destination value,
   original sent value if available, source/destination change evidence,
   proposed action, reason, and concurrency token. Mark equal values unchanged.
3. Treat missing original-write evidence as a manual conflict. An empty field
   alone does not establish Portal ownership. Use authoritative FFP Manager
   field audit history to identify independent edits; preserve them. Legacy
   unknown must never become None automatically or erase a confirmed answer.
4. Obtain explicit approval for the exact reviewed preview, its hash and scope.
   Revalidate the target and verified backup immediately before execution.
   Use a bounded transaction and parameterized UPDATE by immutable destination
   ID plus ExternalRequestId, matching previewed old values and concurrency
   evidence. Abort on any changed value, ambiguous identity or row-count mismatch.
5. Update only approved fields through the supported FFP Manager audit/update
   mechanism. Preserve references, statuses, documents and existing history;
   append actor/reason/before/after evidence. Do not call create-only submission
   retries as an update mechanism. No INSERT, DELETE, reset or schema migration.
6. Produce post-update read-only verification and an audited change ledger.
   Roll back the bounded transaction on verification failure. Any later
   compensation must compare against the repair's written values so subsequent
   independent changes survive.

No bulk correction was executed. Field repair and new-submission deployment
remain separate activities.

## Validation completed

`dotnet build RequestPortal.slnx -c Release --no-restore -m:1` succeeded with
zero warnings and errors, including RequestPortal.Web and FfpManager.Bridge.
The full Release suite used a newly initialized, disposable localhost PostgreSQL
cluster on port 6543: 19 domain tests and 559 infrastructure tests passed;
zero failed. Ten opt-in live SQL tests were skipped because FFPMANAGER_TEST
was unavailable. FFP was not accessed. New Children Under 18 transfer tests
remain pending with the mapping implementation. No production acceptance
submission has been created.
