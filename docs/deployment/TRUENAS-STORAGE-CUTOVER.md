# Request Portal TrueNAS storage cutover

This procedure is Request Portal only. PostgreSQL remains `requestportal` on
`axios.fftpj.local` (`10.10.0.226`, primary). Do not change FFPJM, deploy the
FFP Manager Bridge, create a share/dataset, or delete SharePoint documents.

## Evidence and authorization gates

The approved existing SMB source is `//nas01.fftpj.local/ffpmanager_data`, mounted
on Kratos at `/mnt/ffpmanager-data`. The physical Supporting Documents root must
come from the private NAS layout report and running Bridge configuration. Do not
infer its spelling from the mount name or create a second Supporting Documents root.

Reserve `<verified supporting root>/RequestPortal/files` for Common.Storage and
the sibling `source-archive` for original SharePoint sidecars, unmanaged files and
native history. Existing logical keys remain unchanged beneath `files`; the
physical RequestPortal subdirectory does not rename database references.

`Inspect-RequestPortalStorage-Kratos.py`, rendered with a validated manifest SHA,
copies and verifies a root-owned inventory bundle, imports only the pinned existing
environment helper, and runs the inventory as `aegis`. It does not restart the
application, change the provider, apply migrations, or write/delete source files.
Its private state and export contain document identifiers and must not be published.
The console prints only a structured export path, hash and status.

The administrator must explicitly authorize a concrete `CutoverPlan` after reviewing
source inventory, permission proof, destination verification, integrity exceptions,
database reconciliation, release commit and restore evidence. A generic instruction
to continue does not replace approval of this production cutover plan.

## Least privilege preparation

Inspect CIFS uid/gid, mode, ACL, `multiuser` and symlink options first. Do not change
the share's permissions or globally add `aegis` to the Bridge group. The proposed
dedicated no-login `aegis-requestportal` identity uses a **per-service** supplementary
group and a private mount namespace exposing only its `files` directory at
`/var/lib/aegis/requestportal/supporting-documents`. The original mount is inaccessible
inside that namespace. Render `deploy/storage/*.template` only from verified evidence.
If the mount uses per-user SMB credentials or incompatible ACLs, resolve that before
rendering. The templates alone are not evidence that least privilege works.

Before activation, exercise create/read/update/delete and SHA-256 through the actual
service identity inside the proposed namespace. Prove the original share, parent
Supporting Documents directory, sibling documents, source archive, and other process
roots are inaccessible. Check Portal-specific registration, secrets, keyring and
data-protection access under the new identity; change only Portal-scoped access.
Require the existing mount and its exact CIFS identity; never fall back to a local
directory when NAS is absent. Test namespace configuration with systemd before
installing the production override.

## Resumable preparation

Publish the migration tool for `linux-x64` from the reviewed commit and pin every
artifact. Run it under the approved environment mechanism, never with secrets in
command-line arguments. Its private state is below
`/var/lib/requestportal-storage-migration/`. A migration namespace exposes only the
dedicated `RequestPortal` subtree, with `files` as the destination. Set
`REQUESTPORTAL_MIGRATION_EXPECTED_MOUNT_ROOT` to the verified share-relative subtree.
The tool checks host, DNS, actual PostgreSQL backend address, database, primary state,
and scoped CIFS mount identity before accessing the target.

The tool modes are:

1. `inventory --state <private state>`: provisional, paginated source inventory and
   PostgreSQL document/profile reference reconciliation. No document payload copy.
2. `copy --state <same state> --destination <scoped alias>/files`: paced serial reads,
   persistent Retry-After backoff, per-key/version checkpoints, exact SHA-256 and
   metadata verification. It archives original sidecars/native history outside
   `files`. Resume with the same command; unchanged verified objects are not copied again.
3. `verify --state <same state> --destination <scoped alias>/files`: fresh source
   inventory, every managed target version/current hash, metadata, native-history
   completeness and database associations. No provider change.

Treat live inventory as provisional. Never claim zero missing/corrupt files before
reading actual evidence. A mismatch is recorded with expected/actual SHA and key in
the protected ledger. Only historical bytes matching the declared expected SHA may
recover a file. Recovery still requires explicit review in the authorized plan.
Missing bytes, unresolved mismatches, association conflicts, foreign application
metadata, source changes, archive gaps or target conflicts block cutover.

## Authorized cutover

Render and pin a plan identifying the exact candidate commit, source drive/folder,
source/reference fingerprints, destination alias/mount root, state path, expiry and
reviewed integrity fingerprints. Preserve sources; disallow source deletion, FFPJM
modification and Bridge deployment. If permitted, final deltas still require complete
hash verification and reconciliation; authorization never permits integrity bypass.

After explicit administrator authorization:

1. Verify the previous installed artifact, `/health`, systemd running state, actual
   PostgreSQL primary and applied migration history. Verify the existing protected
   backup SHA and completed isolated restore evidence. Do not create another backup
   merely to repeat completed verification.
2. Freeze Portal writers using the reviewed maintenance procedure; verify no other
   process writes to its SharePoint subtree. Stop Portal. Copy the final delta.
3. Run `final-fence` with the same state/destination. It requires inactive/dead systemd,
   zero main/control PIDs, complete target verification and two identical full source
   inventories and PostgreSQL reference sets. It does not stop the service itself.
4. Apply only reviewed idempotent SQL for
   `20261106221543_PersistRequestLocations`, if absent. Baseline
   `20261106170310_ManagementDashboardAggregate` must already exist. Confirm table
   ownership, schema/trigger/function privileges and outbox trigger compatibility.
5. Run `import-locations --state ... --destination ... --plan <plan.json>
   --authorize-cutover <exact plan SHA256>`. This independently repeats the final
   fence, verifies plan scope/commit/expiry and imports verified legacy locations to
   PostgreSQL with provenance. It preserves newer rows and derives only the existing
   parish/community fallback from source request text. All locations and the import
   marker commit atomically. It never applies schema or rewrites document references.
6. Activate the verified Portal-only identity/mount/environment overrides, including
   the exact authorized plan SHA. New production binaries refuse automatic location
   schema creation or missing/mismatched import evidence. Keep SharePoint source
   credentials/configuration protected and available for reconciliation.
7. Deploy the validated GitHub main payload with the installed, hash-verified
   Aegis.Operations `aegis-deploy --source <verified payload> --apps requestportal`
   procedure. Do not invoke the Bridge deployment wrapper. Verify deployed artifact
   hashes against the exact main commit.

## Acceptance and recovery

Require systemd active/running, Kestrel, HTTPS `/health` 200, authenticated staff
Requests default routing, Management Dashboard load and measured response time.
Require `/health/dashboard` ready, version greater than zero, successful PostgreSQL
source reconciliation and a subsequent automatic publication. Record readiness age,
last success, worker state and pending events. No dashboard path or refresh builder
uses SharePoint for location reads after this change; aggregates stay PostgreSQL-backed
with advisory locking, durable outbox events and atomic publication.

Exercise new upload/download/hash/version checks, own-file CRUD, unrelated-file
denial and NAS failure behavior. Verify existing keys/metadata/associations and source
inventory counts, then check startup/worker logs privately for errors. Do not reopen
writes or declare cutover complete until acceptance passes.

On application failure, use the installed approved application rollback procedure
and restore the reviewed Portal-only configuration as appropriate. Never roll back
business data or drop authoritative location tables; this migration deliberately
refuses `Down`. Provider rollback is safe only while no NAS-only writes have been
accepted. After new NAS writes, freeze writers and reconcile before choosing a
provider; never point back to an incomplete SharePoint copy.

SharePoint retirement/deletion is outside this procedure. Keep sources intact until
complete reconciliation and a separately authorized retirement.
