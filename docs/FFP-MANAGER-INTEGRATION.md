# FFP Manager Integration

## Purpose

RequestPortal is the public/requestor-facing system. FFP Manager remains the authoritative internal system for Application Letter review, approval and fulfilment.

The integration boundary is an HTTPS bridge. RequestPortal does not connect directly to the FFP Manager database.

Development topology:

```text
RequestPortal 10.90.0.200
        |
        | HTTPS + X-FFP-Integration-Key
        v
FFP Manager Bridge 10.10.0.x
        |
        | database-native access (SQL adapter pending schema mapping)
        v
FFP Manager DB 10.10.0.225
```

The firewall should allow RequestPortal to reach only the bridge HTTPS port. The FFP Manager database should allow database access from the bridge host, not from RequestPortal.

## Implemented before schema mapping

The following pieces are implemented and do not depend on FFP Manager table names:

- stable integration contracts for applications, requested items, documents, status and correspondence;
- HTTPS bridge client from RequestPortal;
- bridge API host;
- shared-secret authentication on both directions;
- bridge health endpoint and RequestPortal startup diagnostic;
- durable RequestPortal outbox trigger after a request is submitted;
- a separate deterministic integration outbox message so notification delivery and FFP Manager submission retry independently;
- idempotency using the RequestPortal `IntegrationLink.Id` as the application correlation id;
- master request + requested-item packaging;
- supporting-document byte transfer;
- configurable artifact-size enforcement at the bridge;
- bridge status query and background status polling;
- authenticated status callbacks into RequestPortal;
- callback request/application identity validation against the stored `IntegrationLink`;
- canonical status translation for `MORE_INFORMATION_REQUIRED`, `APPROVED`, `REJECTED`/`DECLINED`, `PENDING_FULFILMENT`, `FULFILLED` and `CLOSED`;
- duplicate callback protection using the RequestPortal inbox;
- FFP Manager information-request callbacks;
- RequestPortal notification/outbox projection for information requests;
- requestor information-response page with supporting-artifact selection;
- requestor response and artifact transfer back through the bridge;
- fake FFP Manager gateway for end-to-end development;
- fake-gateway snapshots and contract tests for application identity, artifacts, status and information responses;
- a production guard that refuses to run the bridge with the fake gateway;
- deliberately blocked SQL gateway boundary for the real schema implementation.

## Schema-dependent work

`SqlFfpManagerGateway` is intentionally not implemented until the real FFP Manager schema/sample data is supplied. It will map the stable contracts to the real:

- Application Letter master table;
- Items Requested details;
- Supporting Documents details/storage;
- approval/status/history structures;
- staff request-for-information/correspondence structures;
- numbering/audit/stored-procedure/trigger rules.

No RequestPortal code should contain those table names or database-specific rules. Only `SqlFfpManagerGateway` should understand the FFP Manager schema.

## Schema handoff package

The minimum package needed to complete `SqlFfpManagerGateway` is:

1. schema for Application Letter, Items Requested and Supporting Documents;
2. primary keys, foreign keys, indexes and defaults for those objects;
3. triggers attached to those objects;
4. stored procedures/functions/views used when creating or updating an Application Letter;
5. workflow/status/history tables and their lookup values;
6. correspondence/additional-information tables or procedures;
7. 5-10 anonymized applications representing new, in-review, information-required, approved, declined and completed cases;
8. related item/document/status/correspondence rows for the same sample application IDs;
9. a test-only connection string or a locally configured secret containing it.

The SQL account should be test-only and initially read-only. Write permissions can be added only after the creation path and hidden FFP Manager business rules are understood.

## RequestPortal configuration

Development settings can be supplied with User Secrets:

```powershell
dotnet user-secrets set "FfpManagerIntegration:Enabled" "true"
dotnet user-secrets set "FfpManagerIntegration:BaseUrl" "https://10.10.0.200:7443/"
dotnet user-secrets set "FfpManagerIntegration:SharedSecret" "<same-long-random-secret>"
```

In production, `FfpManagerIntegration:SharedSecret` is hydrated through Common.Secrets/OpenBao. Production startup fails closed if the integration is enabled without a bridge URL and shared secret.

## Bridge configuration

For local/internal development set the bridge values with environment variables or user-local configuration. Do not commit the secret.

```text
Bridge__SharedSecret=<same-long-random-secret>
Bridge__RequestPortalBaseUrl=https://10.90.0.200/
Bridge__Gateway=Fake
Bridge__MaxArtifactBytes=26214400
```

Start the bridge:

```powershell
dotnet run --project .\RequestPortal\FfpManager.Bridge\FfpManager.Bridge.csproj
```

The fake bridge gateway is in-memory. Restarting the bridge clears fake FFP applications. The bridge health endpoint reports the active gateway and artifact limit:

```text
GET /health
```

Production refuses `Bridge__Gateway=Fake`. After schema mapping, use `Bridge__Gateway=Sql` and supply the SQL connection string through the approved secret provider rather than source control.

## End-to-end development flow

1. Enable the integration in RequestPortal.
2. Start RequestPortal and `FfpManager.Bridge`.
3. Confirm the RequestPortal startup diagnostics report `FFP Manager Bridge` as reachable.
4. Submit an assistance request in RequestPortal.
5. The RequestPortal database transaction writes `RequestSubmittedMessage` to the outbox.
6. The outbox dispatcher creates a deterministic `FfpManagerSubmissionRequestedMessage` and independently sends the normal portal notification.
7. The integration outbox message submits the application to the bridge.
8. The fake bridge returns an `APP-nnnn` external reference. RequestPortal stores it in `integration_links`.
9. Uploaded RequestPortal documents are transferred to the same fake application.

### Simulate an FFP Manager status change

In Development the bridge exposes a test endpoint:

```http
POST /api/dev/applications/APP-1001/status
X-FFP-Integration-Key: <shared-secret>
Content-Type: application/json

{
  "statusCode": "APPROVED",
  "statusText": "Approved",
  "detail": "Approved by the FFP Manager test workflow.",
  "isTerminal": false
}
```

The bridge pushes the resulting status to:

```text
POST /api/integration/ffpmanager/status
```

RequestPortal authenticates the callback, verifies the FFP application id belongs to the stated RequestPortal request, deduplicates the message, applies the canonical lifecycle transition and writes the normal status-notification outbox event.

### Simulate a request for more information

```http
POST /api/dev/applications/APP-1001/information-requests
X-FFP-Integration-Key: <shared-secret>
Content-Type: application/json

{
  "message": "Please provide a current quotation and beneficiary list.",
  "requestedBy": "Application Officer",
  "requestedArtifacts": ["Quotation", "Beneficiary list"]
}
```

The bridge pushes the request to:

```text
POST /api/integration/ffpmanager/information-requests
```

RequestPortal validates the application/request identity, changes a submitted request to `MoreInformationRequired`, records the reason in status history, and queues a requestor notification. The Request Details page presents a response form and allows the requestor to select uploaded supporting documents. The response and selected artifacts are sent back to the same FFP Manager application through the bridge.

## Status contract

Before the real FFP Manager status table is mapped, the bridge uses these canonical integration codes:

- `RECEIVED`
- `MORE_INFORMATION_REQUIRED`
- `APPROVED`
- `REJECTED` or `DECLINED`
- `PENDING_FULFILMENT`
- `FULFILLED`
- `CLOSED`

The SQL adapter will translate actual FFP Manager codes into these contract values instead of leaking database codes into RequestPortal.

## Notification channels

RequestPortal currently models Email, SMS and WhatsApp notification preferences. Email delivery is commissioned through the current notification sender. SMS and WhatsApp require their delivery providers to be commissioned; the FFP Manager integration must not pretend those channels were delivered when no provider exists. The information request remains visible in RequestPortal regardless of external notification delivery.

## Security rules

- RequestPortal never receives FFP Manager database credentials.
- The bridge database account will be least-privilege and test-only during development.
- RequestPortal and bridge use HTTPS.
- Both directions require `X-FFP-Integration-Key` when configured.
- Callback application identity must match the RequestPortal `IntegrationLink`.
- Production must obtain secrets outside source control.
- Production bridge startup rejects the fake gateway, wildcard AllowedHosts and non-HTTPS RequestPortal callback URLs.
- Bridge artifact size is bounded and descriptor size must match the uploaded file length.
- The bridge should be firewalled so only RequestPortal and approved administration sources can reach it.
- The FFP Manager database should accept connections from the bridge host only, not RequestPortal.

## Definition of complete integration

After schema mapping, the integration is complete when a RequestPortal request can be created as a normal FFP Manager Application Letter with items and supporting artifacts, pass through the existing FFP Manager approval process without special handling, return all meaningful status changes to RequestPortal, carry staff information requests to the requestor, and return requestor responses/artifacts to the same FFP Manager Application Letter without duplicate records.
