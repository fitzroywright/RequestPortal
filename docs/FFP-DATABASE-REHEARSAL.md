# JMDB01 / FFP production-style bridge rehearsal

This is a **rehearsal configuration**, not a declaration that FFPJM is commissioned.
Treat JMDB01/FFP as the writable target. Do not point the bridge at FFPJM, change
the Request Portal PostgreSQL database, or reset the shared XPO counter.

## Confirmed components

- Request Portal calls the separately deployed bridge over authenticated HTTPS
  using `FfpManagerIntegration:Enabled` and `FfpManagerIntegration:BaseUrl`.
- Production bridge selects `Bridge:Gateway=Sql` and retrieves its SQL string
  from Common.Secrets name `FfpManagerBridge:SqlConnectionString`.
- The bridge writes ApplicationLetters, ItemsRequested and SupportingDocument;
  the normal callback outbox resides in the FFP database.
- SQL migration 000 adds the nullable, unique `ExternalRequestId` correlation key.
- SQL migration 001 installs the integration outbox and enqueue procedures.
- The bridge reference-number/notification changes are in draft PR #50, **not**
  on main. Build, counter schema compatibility and real DB tests are pending.

## Safe order of operations

1. Run `Sql/002_FFP_RehearsalPreflight.sql` in SSMS against JMDB01/FFP.
   It is intentionally read-only and rejects other databases/servers.
2. Review any missing objects or incompatible columns. Apply 000 then 001
   **only in JMDB01/FFP** and only after object comparison and backup.
3. Verify the real XPO `IDGeneratorTable` counter and FFP Manager
   `ApplicationLetter` numbering behavior. The PR #50 real SQL tests already
   reject targets other than JMDB01/FFP. Do not rely on a skipped test.
4. Build and test the PR #50 branch (Portal and bridge), including SQL counter,
   duplicate-request, notification and PostgreSQL migration tests.
5. Determine where the bridge actually runs. The Request Portal Kratos systemd
   unit does **not** deploy or restart the separate bridge.
6. Update the bridge's existing production Common.Secrets connection string
   to **JMDB01/FFP**. Keep the working Integrated Security/Kerberos, TLS and
   other connection-string attributes. Check the service identity's access.
7. Confirm the bridge uses a Linux-accessible mounted TrueNAS path and that
   `SupportingDocument.FileName` will be usable from FFP Manager on Windows.
8. Set the bridge's HTTPS RequestPortalBaseUrl, production secret, SQL gateway,
   reference-data mappings, service identity and non-wildcard AllowedHosts.
   Restart **that bridge service** and verify `/ready` returns HTTP 200.
9. Set Request Portal's FfpManagerIntegration:BaseUrl to the actual bridge
   HTTPS endpoint and Enabled=true; preserve the shared callback secret.
   Restart the Request Portal service only if its runtime settings changed.
10. Submit a single controlled rehearsal request, verify its genuine FFP
    reference (not the RP code copied into ReferenceNo), duplicate prevention,
    item rows, attachments, outbox state, and provider delivery logs. Confirm
    reverse status/information sync before increasing volume.

## Source-level gaps discovered in FFP Manager

The current FFP Manager `Common/FFP.Module/Controllers/Data/ApplicationLetterViewController.cs`
contains a `RequestPortalRequestInformation` action which directly executes
`RequestPortal_EnqueueInformationRequest`. Its `OnObjectCommitting` handler
generates reference numbers and attaches letters, but does **not** enqueue
`RequestPortal_EnqueueApplicationStatusChanged`. The action's procedure call
does not visibly share the transaction that persists any related business
change. Therefore, the full same-transaction event guarantee described in
`docs/FFP-MANAGER-INTEGRATION-COMMISSIONING.md` is not yet demonstrated.

The correct fix requires tracing the actual XPO/ObjectSpace transaction boundary,
including batch/background status modifications, not simply invoking the
stored procedure in a UI controller after a save. Do not introduce a trigger
or a second unsynchronized transaction as a shortcut.

## Acceptance evidence required

- SQL Server JMDB01, initial catalog FFP, verified service principal.
- Migrations 000/001 present and schema diagnostics healthy.
- Counter allocation uses existing FFP Manager ApplicationLetter generator.
- Two deliveries of one Portal RequestId link to exactly one ApplicationLetter.
- Source Code and FFP ReferenceNo are distinct and properly recorded.
- TrueNAS bytes and FFP Manager usable path confirmed.
- Outbound status events emitted atomically with FFP status changes, delivered
  and deduplicated in Portal; information responses work both ways.
- Customer Service email and opt-in applicant notification confirmed at provider.
- No FFPJM writes; no user data exposed in commissioning logs.
