# One-time PostgreSQL backup and deployment verification access

This closes the backup/preflight access gap for the already merged dashboard
migration. It does not change application behavior, the Bridge, production
business records, OpenBao settings, existing sudo grants or SSH host trust.

The reviewed installer prepares two root-owned, manifest-pinned commands. Merely
installing their files grants no execution permission. An administrator must run:

```sh
sudo /usr/local/sbin/aegis-requestportal-postgres-preflight
```

That command accepts no arguments and only runs on Kratos. It:

1. Verifies every compiled dependency against its pinned manifest.
2. Copies only required Common.Secrets/bootstrap settings from the running Portal,
   drops to its existing aegis identity and resolves the existing PostgreSQL secret.
3. Verifies axios.fftpj.local, 10.10.0.226, requestportal, primary-server status,
   migration history and the privileges required by the additive migration.
4. Installs only the matching PostgreSQL client package if absent, using existing
   trusted APT repositories. It installs no database server and changes no SSH trust.
5. Exports a read-only repeatable-read snapshot, records table counts and migration
   history, and runs pg_dump against that same snapshot. This makes verification
   counts consistent even while normal production activity continues.
6. Retains the checksummed custom-format archive under root-only
   `/var/backups/aegis-requestportal-postgres/`. Provider errors, credentials and
   applicant values are never printed.
7. Runs the already prepared read-only historical input-quality count audit.
8. Enables exactly one additional sudo command for fwright:
   `/usr/local/sbin/aegis-requestportal-dashboard-check`, with no arguments,
   NOSETENV and an exact SHA-256 executable digest. Existing grants are untouched;
   a conflicting existing grant causes a stop. The new grant is syntax-checked
   before installation and permits only the read-only aggregate check described below.
9. Creates an operator-only export copy for encrypted SSH transfer to this
   workstation's existing PostgreSQL 18 isolated restore verifier. Both directory
   and files are private. Production credentials are not exported. The database
   archive itself contains sensitive production records and must remain protected.

The administrator output includes archive/report checksums, the private export
path and the root-only backup evidence path. It says
`backup-created-awaiting-isolated-restore`; this is not deployment approval or a
claim that restoration has already been verified.

Afterward the agent verifies transfer checksums, restores the archive into a new
operator-only PostgreSQL cluster with TCP disabled, compares every source table
count and migration ID, and shuts down/removes only that disposable cluster.
Ownership, ACLs and tablespaces are not replayed in the isolated environment;
the original archive retains that metadata. No Portal process, notification
worker or Bridge runs against the restored database. Existing local test databases
are not reset. The original production backup remains root-only on Kratos.

Only after restore verification and migration preflight pass may the existing
Aegis.Operations Request Portal-only deployer be run. It retains its ordinary
application backup and automatic rollback procedure.

The additional read-only command resolves the same service secret and opens a
READ ONLY transaction. It verifies the actual production target, current
published aggregate, reconciled totals, refresh metadata and pending refresh
counts. It measures first/repeat production service reads and forbids storage
access. It emits aggregate metadata only; it cannot execute migrations, produce
backups, submit applications or process queues. These timings are backend service
measurements, not authenticated browser load timings.

The commands preserve the existing hash-pinned integration audit. They do not use
deployment/install permissions to execute an unapproved root payload. The backup
command requires actual administrator authentication; its narrow read-only grant
is created only when that administrator explicitly runs the reviewed command.

Validation completed before staging: tool Release builds without warnings/errors,
five Python guard tests pass, and a synthetic full backup/isolated restore verifies
table counts and migration history without production access. No production
backup, grant, migration or deployment is claimed until the administrator command
has actually run and its output has been verified.
