# Focused dashboard geography recovery

Request locations and published dashboard aggregates belong in PostgreSQL
`requestportal` on the approved Axios primary. Supporting documents continue to use
the existing SharePoint provider. This recovery does not inspect NAS contents,
transfer supporting documents, change providers, modify FFPJM, or deploy the Bridge.

The runtime calculator and drilldowns read the `request_locations` table in a batch,
with the existing PostgreSQL request-text fallback. New resolved locations save to
PostgreSQL. Its statement trigger enqueues a dashboard event in the same transaction.
The existing worker, advisory lock, atomic snapshot publication, reconciliation and
last valid snapshot remain in place. Application and dashboard health semantics are
unchanged.

## Read-only evidence and geography preparation

The manifest-pinned `Read-RequestPortalDashboardStorage-Kratos.py` reuses the installed
approved managed-environment helper and runs tools under the existing application
identity. It classifies the first failing metadata/content operation in a bounded
64-request sample. It then exports only `requestportal/locations` and the matching
Common.Storage metadata. No supporting-document inventory is performed.

The geography exporter verifies the configured application root, request identity,
metadata identity, payload size and SHA-256. On a mismatch it checks content ETag and
metadata again, distinguishing a concurrent source write from a stable integrity
failure without downloading the failed payload repeatedly. Private export files
retain coordinates, precision, original resolution time, key and declared SHA.
Unknown requests, missing content, invalid JSON and unresolved integrity failures
block recovery. Source files remain untouched. Secrets and provider bodies are never
included in the console result; the private export must remain protected.

## Production application transition

Before any write, verify the installed release and ordinary application health,
the actual approved PostgreSQL primary, existing migration history, schema/function/
trigger privileges, the existing protected backup hash and completed isolated restore
evidence. Reuse that evidence; do not create another database backup unnecessarily.

Use the established administrator and Aegis.Operations procedure for these steps:

1. Preserve the read-only diagnostic evidence and resolve its specific source error.
   Obtain a complete verified geography export before entering maintenance.
2. Stop Request Portal writers. Run `location-export` again into the same protected
   `locations.json`; unchanged payloads can be reused only after source metadata,
   content ETag and the cached payload hash match. Export records whether the service
   was inactive/dead with zero main/control PIDs before and after the read.
3. After backup/restore and target checks pass, run `location-schema` against that
   export. It refuses active writers, an unfenced/stale export or unexpected pending
   migrations. It applies only `20261107000005_PostgresRequestLocations`, if absent.
4. Run `location-import` against the final export. Import preserves original geography
   and newer PostgreSQL rows. Location rows, provenance, the completion marker and
   release of the old retry cooldown commit atomically under import and worker locks.
   The previous aggregate remains failed until a real rebuild succeeds.
5. Deploy the exact validated GitHub main payload through the installed
   `aegis-deploy` with `--apps requestportal`. Keep the existing SharePoint provider.
   Production startup refuses an unprepared geography schema or missing import proof.
6. Verify installed artifact/commit, systemd active/running, Kestrel, `/health` 200,
   `/health/dashboard` 200, PostgreSQL source reconciliation and repeated snapshot
   version advances. Verify Requests default routing and dashboard responses.
   Inspect startup/worker errors privately. Do not declare completion before these pass.

On application failure use the approved application rollback procedure. Retain the
PostgreSQL tables and business data; this migration intentionally refuses `Down`.
If preparation fails while writers are stopped, restore the previous healthy
application service. Do not repeat a failed deployment unchanged.

TrueNAS directory/permission preparation remains deferred until dashboard recovery
passes. Any future document-provider cutover requires separate explicit administrator
authorization and readable existing references.
