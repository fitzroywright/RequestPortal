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

## Implemented before schema mapping

The following pieces are implemented and do not depend on FFP Manager table names:

- stable integration contracts for applications, requested items, documents, status and correspondence;
- HTTPS bridge client from RequestPortal;
- bridge API host;
- shared-secret authentication on both directions;
- durable RequestPortal outbox trigger after a request is submitted;
- idempotency using the RequestPortal `IntegrationLink.Id` as the application correlation id;
- master request + requested-item packaging;
- supporting-document byte transfer;
- bridge status query;
- authenticated status callbacks into RequestPortal;
- canonical status translation for `MORE_INFORMATION_REQUIRED`, `APPROVED`, `REJECTED`/`DECLINED`, `PENDING_FULFILMENT`, `FULFILLED` and `CLOSED`;
- duplicate callback protection using the RequestPortal inbox;
- FFP Manager information-request callbacks;
- RequestPortal notification/outbox projection for information requests;
- requestor information-response service with supporting artifacts;
- fake FFP Manager gateway for end-to-end development;
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
```

Start the bridge:

```powershell
dotnet run --project .\RequestPortal\FfpManager.Bridge\FfpManager.Bridge.csproj
```

The current bridge gateway is in-memory. Restarting the bridge clears fake FFP applications.

## End-to-end development flow

1. Enable the integration in RequestPortal.
2. Start RequestPortal and `FfpManager.Bridge`.
3. Submit an assistance request in RequestPortal.
4. The RequestPortal database transaction writes `RequestSubmittedMessage` to the outbox.
5. The outbox dispatcher submits the application to the bridge and then sends the normal RequestPortal submission notification.
6. The fake bridge returns an `APP-nnnn` external reference. RequestPortal stores it in `integration_links`.
7. Uploaded RequestPortal documents are transferred to the same fake application.

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

RequestPortal deduplicates the callback, applies the canonical lifecycle transition and writes the normal status-notification outbox event.

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

RequestPortal changes the request to `MoreInformationRequired` when appropriate, records the reason in status history, and queues a requestor notification.

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

## Security rules

- RequestPortal never receives FFP Manager database credentials.
- The bridge database account will be least-privilege and test-only during development.
- RequestPortal and bridge use HTTPS.
- Both directions require `X-FFP-Integration-Key` when configured.
- Production must obtain secrets outside source control.
- The bridge should be firewalled so only RequestPortal and approved administration sources can reach it.
- The FFP Manager database should accept connections from the bridge host only, not RequestPortal.

## Definition of complete integration

After schema mapping, the integration is complete when a RequestPortal request can be created as a normal FFP Manager Application Letter with items and supporting artifacts, pass through the existing FFP Manager approval process without special handling, return all meaningful status changes to RequestPortal, carry staff information requests to the requestor, and return requestor responses/artifacts to the same FFP Manager Application Letter without duplicate records.
