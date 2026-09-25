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
        | Microsoft.Data.SqlClient + SMB file share
        v
FFP Manager DB 10.10.0.225
```

The firewall should allow RequestPortal to reach only the bridge HTTPS port. The FFP Manager database should allow database access from the bridge host, not from RequestPortal.

## Implemented

The following pieces are implemented:

- stable integration contracts for applications, requested items, documents, status and correspondence;
- HTTPS bridge client from RequestPortal;
- bridge API host;
- shared-secret authentication on both directions;
- bridge health endpoint and RequestPortal startup diagnostic;
- durable RequestPortal outbox trigger after a request is submitted;
- a separate deterministic integration outbox message so notification delivery and FFP Manager submission retry independently;
- master request + requested-item packaging;
- supporting-document byte transfer;
- configurable artifact-size enforcement at the bridge;
- bridge status query and background status polling;
- authenticated status callbacks into RequestPortal;
- callback request/application identity validation against the stored `IntegrationLink`;
- duplicate callback protection using the RequestPortal inbox;
- FFP Manager information-request callbacks;
- RequestPortal notification/outbox projection for information requests;
- requestor information-response page with supporting-artifact selection;
- requestor response and artifact transfer back through the bridge;
- fake FFP Manager gateway for isolated development;
- SQL Server gateway for the real FFP Manager schema;
- a production guard that refuses to run the bridge with the fake gateway.

## Real FFP Manager mapping

`SqlFfpManagerGateway` maps the stable bridge contract to the supplied FFP Manager schema:

- `ApplicationLetters` is the master application record.
- `ApplicationLetters.ExternalRequestId` stores the RequestPortal request GUID and is used as the idempotency/reverse-correlation key.
- `ItemsRequested.ApplicationLetterID` links requested items to the application.
- `ItemsRequested.UnitOfMeasure` is resolved through `UnitsOfMeasure` by `Code` or `Name`.
- `SupportingDocument.ApplicationLetter` links artifacts to the application.
- `SupportingDocument.Oid` stores the RequestPortal document GUID so retries update the same document metadata row.
- `SupportingDocument.FileName` stores the full UNC path to the copied file.
- the default supporting-document root is `\\fftpj.local\ffp\FFPManagerData$\Supporting Document` and is configurable.
- `SupportingDocument.DocumentType` is resolved through `DocumentTypes`; RequestPortal-to-FFP mappings are configurable.
- `ApplicationLetters.ApplicationStatus` is read through `ApplicationStatusTypes`.
- `ApplicationNotes` stores requestor information responses, using an idempotent message-derived `Code` and configurable `NoteType`.

Application creation and requested-item inserts occur in one serializable SQL transaction. A retry for the same RequestPortal request returns the existing FFP Manager Application Letter rather than creating a duplicate.

## Remaining data needed for final production mapping

The SQL adapter is operational, but these lookup/workflow values still need representative FFP Manager data before production commissioning:

1. `ApplicationStatusTypes` rows used for received/new, information required, approved, declined/rejected, pending fulfilment, fulfilled and closed;
2. `DocumentTypes` rows that should correspond to RequestPortal document types, or one agreed fallback document type;
3. sample `ApplicationNotes` rows created by FFP Manager staff when requesting information from an applicant, including the `NoteType` convention;
4. confirmation of the FFP Manager account values desired for `UserName` and `CreatedBy`;
5. confirmation that no trigger/stored procedure must be invoked instead of direct inserts into these tables.

Unknown legacy values are configuration-driven rather than guessed.

## RequestPortal configuration

Development settings can be supplied with User Secrets:

```powershell
dotnet user-secrets set "FfpManagerIntegration:Enabled" "true"
dotnet user-secrets set "FfpManagerIntegration:BaseUrl" "https://10.10.0.200:7443/"
dotnet user-secrets set "FfpManagerIntegration:SharedSecret" "<same-long-random-secret>"
```

In production, `FfpManagerIntegration:SharedSecret` is hydrated through Common.Secrets/OpenBao. Production startup fails closed if the integration is enabled without a bridge URL and shared secret.

## Bridge configuration

For internal SQL-backed testing:

```text
Bridge__Gateway=Sql
Bridge__SharedSecret=<same-long-random-secret>
Bridge__RequestPortalBaseUrl=https://10.90.0.200/
Bridge__SqlConnectionString=<test FFPJM SQL Server connection string>
Bridge__SupportingDocumentRoot=\\fftpj.local\ffp\FFPManagerData$\Supporting Document
Bridge__CreatedBy=RequestPortal
Bridge__UserName=RequestPortal
Bridge__MaxArtifactBytes=26214400
```

Optional legacy lookup mappings can be supplied in configuration. Example only:

```json
{
  "Bridge": {
    "InitialApplicationStatus": "<real FFP status code or name>",
    "DefaultDocumentType": "<real FFP document type code or name>",
    "DocumentTypeMap": {
      "RequestSupport": "<FFP code-or-name>"
    },
    "StatusMap": {
      "<FFP status code-or-name>": "APPROVED"
    }
  }
}
```

Do not commit the SQL password or shared secret.

Start the bridge:

```powershell
dotnet run --project .\RequestPortal\FfpManager.Bridge\FfpManager.Bridge.csproj
```

The bridge health endpoint reports the active gateway and artifact limit:

```text
GET /health
```

## End-to-end flow

1. Enable the integration in RequestPortal.
2. Start RequestPortal and `FfpManager.Bridge`.
3. Confirm the RequestPortal startup diagnostics report `FFP Manager Bridge` as reachable.
4. Submit an assistance request in RequestPortal.
5. The RequestPortal database transaction writes `RequestSubmittedMessage` to the outbox.
6. The outbox dispatcher creates a deterministic `FfpManagerSubmissionRequestedMessage` and independently sends the normal portal notification.
7. The integration outbox message submits the application to the bridge.
8. The SQL gateway creates the FFP Manager `ApplicationLetters` row and associated `ItemsRequested` rows, or returns the existing row when the RequestPortal request GUID already exists in `ExternalRequestId`.
9. Uploaded RequestPortal documents are copied to the FFP Manager supporting-document share and upserted into `SupportingDocument`.
10. RequestPortal stores the returned FFP Manager `ApplicationLetters.ID` in `integration_links`.
11. RequestPortal polls the bridge for status; the bridge reads `ApplicationStatusTypes` and maps configured legacy values to the canonical portal lifecycle.
12. Requestor responses are written back to `ApplicationNotes`, while selected artifacts are uploaded to the same Application Letter.

## Status contract

RequestPortal understands these canonical integration codes:

- `RECEIVED`
- `MORE_INFORMATION_REQUIRED`
- `APPROVED`
- `REJECTED` or `DECLINED`
- `PENDING_FULFILMENT`
- `FULFILLED`
- `CLOSED`

`Bridge:StatusMap` translates actual FFP Manager status codes/names into these values. If a mapping is not supplied, the SQL gateway normalizes the FFP status `Code` by uppercasing it and replacing spaces/hyphens with underscores.

## Notification channels

RequestPortal currently models Email, SMS and WhatsApp notification preferences. Email delivery is commissioned through the current notification sender. SMS and WhatsApp require their delivery providers to be commissioned; the FFP Manager integration must not pretend those channels were delivered when no provider exists. The information request remains visible in RequestPortal regardless of external notification delivery.

## Security rules

- RequestPortal never receives FFP Manager database credentials.
- The bridge database account should be least-privilege and test-only during development.
- RequestPortal and bridge use HTTPS.
- Both directions require `X-FFP-Integration-Key` when configured.
- Callback application identity must match the RequestPortal `IntegrationLink`.
- Production must obtain secrets outside source control.
- Production bridge startup rejects the fake gateway, wildcard AllowedHosts and non-HTTPS RequestPortal callback URLs.
- Bridge artifact size is bounded and descriptor size must match the uploaded file length.
- The bridge should be firewalled so only RequestPortal and approved administration sources can reach it.
- The FFP Manager database should accept connections from the bridge host only, not RequestPortal.
- The bridge service identity must have write access to the configured supporting-document SMB share.

## Definition of complete integration

The integration is complete when a RequestPortal request can be created as a normal FFP Manager Application Letter with items and supporting artifacts, pass through the existing FFP Manager approval process without special handling, return all meaningful status changes to RequestPortal, carry staff information requests to the requestor, and return requestor responses/artifacts to the same FFP Manager Application Letter without duplicate records.
