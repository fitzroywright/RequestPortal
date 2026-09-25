# FFP Manager <-> Request Portal Integration Commissioning

## Application correlation identity

Request Portal correlation uses the dedicated nullable
`dbo.ApplicationLetters.ExternalRequestId` column.

The outbox installation script creates the column and a unique index when they are
missing. Request Portal submissions write the Portal Request GUID to
`ExternalRequestId`.

Do not reuse `MessageID` for Request Portal correlation. Existing FFP Manager
meaning attached to `MessageID` remains untouched.

The bridge treats `ExternalRequestId` as the idempotency key:

- a retry with the same Portal Request GUID returns the existing ApplicationLetter
- a new Portal Request GUID creates a new ApplicationLetter
- outbound status and information-request events read the Portal identity from
  `ExternalRequestId`

No automatic backfill from `MessageID` is performed. Any historical Request Portal
records must be reviewed before a migration is approved.

## Durable outbound events

Normal FFP Manager -> Request Portal delivery uses `dbo.RequestPortalIntegrationOutbox`.
The bridge no longer discovers normal status changes by repeatedly scanning
`ApplicationLetters`.

Install:

`FfpManager.Bridge/Sql/001_RequestPortalIntegrationOutbox.sql`

FFP Manager must call the supplied enqueue procedures using the SAME SQL connection
and transaction as the ApplicationLetter business change.

Status change:

```sql
BEGIN TRANSACTION;

UPDATE dbo.ApplicationLetters
SET ApplicationStatus = @ApplicationStatus,
    DateUpdated = SYSUTCDATETIME()
WHERE ID = @ApplicationLetterId;

EXEC dbo.RequestPortal_EnqueueApplicationStatusChanged
    @ApplicationLetterId = @ApplicationLetterId,
    @EventId = @OperationId,
    @OccurredUtc = @OccurredUtc,
    @CreatedBy = @UserName;

COMMIT TRANSACTION;
```

Information request:

```sql
BEGIN TRANSACTION;

-- Persist the FFP Manager business action first.

EXEC dbo.RequestPortal_EnqueueInformationRequest
    @ApplicationLetterId = @ApplicationLetterId,
    @Message = @Message,
    @RequestedBy = @UserName,
    @DueUtc = @DueUtc,
    @EventId = @OperationId,
    @OccurredUtc = @OccurredUtc,
    @CreatedBy = @UserName;

COMMIT TRANSACTION;
```

The same immutable `EventId` must be retained if the user or application retries the
same logical operation. The outbox has a unique constraint on EventId.

The bridge claims committed events with SQL locking, posts them to Request Portal,
marks successful delivery, and retries failures with bounded exponential backoff.
Events that exhaust the retry budget remain visible for Level 1 diagnostics.

Request Portal status reconciliation remains a recovery mechanism on the Request Portal side. It is not the normal FFP -> Portal transport.

## Production secrets and identity

Do not place the SQL connection string or integration shared secret in appsettings.

The bridge resolves these through Common.Secrets:

- `FfpManagerBridge:SqlConnectionString`
- `FfpManagerIntegration:SharedSecret`

The secret names can be changed with:

- `Bridge:SqlConnectionStringSecretName`
- `Bridge:SharedSecretSecretName`

Production Common.Secrets must run in Production mode with its approved production
provider. Secret Zero remains outside source-controlled configuration.

Required non-secret production configuration:

- `Bridge:ServiceIdentity`
- `Bridge:RequestPortalBaseUrl` -- absolute HTTPS
- `Bridge:Gateway=Sql`
- `Bridge:InitialApplicationStatus`
- `Bridge:StatusMap`
- `Bridge:DocumentTypeMap`
- `Bridge:SupportingDocumentRoot` until SharePoint replaces legacy document storage
- `Bridge:EnableOutboxDispatch=true`
- restricted `AllowedHosts`

Request Portal also resolves the callback secret through Common.Secrets using
`FfpManagerIntegration:SharedSecretName`. Its production FFP Manager BaseUrl must be
HTTPS.

## Operational diagnostics

The bridge participates in Common.Diagnostics using Starfleet numbering:

- Level 5 -- configuration, service identity, HTTPS target, SQL/callback secret
  availability, outbox enabled.
- Level 4 -- FFP SQL connectivity and durable outbox schema/procedures.
- Level 3 -- FFP reference-data catalogue and configured default mapping resolution.
- Level 2 -- authenticated Request Portal callback probe.
- Level 1 -- deep durable-recovery state: pending, locked, stale, and exhausted events.

Level 5 is the lightest check. Level 1 is the deepest check.

The Level 1 check is deliberately non-destructive in production. Failure/replay tests
belong in controlled integration testing, not an automatic production diagnostic run.

Request Portal exposes an authenticated callback probe at:

`GET /api/integration/ffpmanager/probe`

The same Common.Secrets-backed integration key used for callbacks protects the probe.


## Health and readiness

The bridge exposes two different operational signals:

- `GET /health` -- process liveness only. It answers whether the bridge process is running.
- `GET /ready` -- integration readiness. In SQL mode it verifies the durable outbox schema and reference-data availability and returns HTTP 503 when those dependencies are not ready.

Use Level 5 -> Level 1 diagnostics for detailed fault isolation. Do not treat the liveness endpoint as proof that the integration is commissioned.
