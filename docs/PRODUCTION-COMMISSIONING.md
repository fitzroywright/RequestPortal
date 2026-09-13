# Request Portal Production Commissioning

This runbook is the minimum production commissioning checklist for Request Portal. Code is designed to fail closed: unavailable dependencies must not silently weaken authorization, document safety, notification delivery, fulfilment consistency, integration consistency, or demo isolation.

## 1. Environment

Set `ASPNETCORE_ENVIRONMENT=Production` on the application host. Production startup intentionally fails closed if durable storage, persistent Data Protection keys, OpenBao, required authentication/email configuration, malware scanning, or EF migrations are not ready.

`Demo:Enabled` **must remain false**. The application now refuses to initialize demo mode when the host environment is Production even if configuration is accidentally changed.

## 2. PostgreSQL and migrations

The solution is flattened. Infrastructure is at `RequestPortal.Infrastructure` and the web startup project is `RequestPortal.Web`.

Restore the pinned EF tool and verify the model before deployment:

```powershell
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project RequestPortal.Infrastructure --startup-project RequestPortal.Web
dotnet ef migrations script --idempotent --project RequestPortal.Infrastructure --startup-project RequestPortal.Web -o artifacts/requestportal-migration.sql
```

Review the generated SQL and take a verified backup before applying it. Production startup calls `Database.MigrateAsync()` and refuses to operate against an unavailable database.

`20260913045000_AddFulfilmentLines` adds the durable partial-fulfilment progress table. It includes foreign keys to fulfilments and request items and backfills existing fulfilments; historical fulfilments already marked Fulfilled are backfilled as fully fulfilled.

Do not promote a disposable demo database by blindly baselining it. Prefer a clean production database created by the committed migration chain.

## 3. OpenBao / Common.Secrets

Production uses Common.Secrets with the OpenBao provider. The non-secret production settings are under `CommonSecrets:OpenBao`, including:

- `Address`
- `MountPath`
- `BasePath`
- `AuthMountPath`
- `RoleId`
- `SecretIdEnvironmentVariable`
- `SecretIdFile`
- token renewal/revocation and HTTPS requirements

The bootstrap Secret ID must be supplied through the configured protected file or environment variable and must never be committed to source control. The Request Portal AppRole policy should have least-privilege read access only to the Request Portal secret subtree.

Expected secrets include the RequestPortal PostgreSQL connection string, commissioned external identity provider credentials, SMTP password, and any enabled integration or mobile-messaging credentials.

## 4. Durable document storage

`Storage:Local:RootPath` must point to durable external storage and not the application deployment directory. The service identity requires only the permissions needed for the Request Portal document root. Back up PostgreSQL and document storage as one recovery set and perform a restore test.

## 5. Persistent Data Protection keys

`Security:DataProtectionKeysPath` must point to durable storage shared by every Request Portal instance. Restrict it to the Request Portal service identity and backup administrators.

## 6. Staff authentication and authorization

Commission the configured staff OIDC provider and validate FFP group claims against the configured reviewer and administrator groups. Test an authorized reviewer, an authorized administrator, an ordinary FFP user, and a disabled/locked account. The authorization boundary must deny staff privileges when required claims or group membership are missing.

## 7. External representative identity

Commission at least one supported external identity provider. Store secrets in OpenBao. Test new sign-in, returning sign-in, verified-email linking, logout, callback failure, and non-local return URL rejection.

## 8. Notifications

Email is the required baseline production notification channel. Configure SMTP host/port/from address/username in production configuration and keep the password in OpenBao.

Production defaults explicitly set:

```json
"Notifications": {
  "Enabled": true,
  "SmsEnabled": false,
  "WhatsAppEnabled": false
}
```

Request Portal now contains the application plumbing for SMS and WhatsApp, and Common.Messaging contains provider-neutral adapters for both channels. They remain optional and disabled by default. Email continues to carry mandatory workflow notices; an enabled user preference may add WhatsApp and/or SMS delivery for the same event.

The mobile adapters deliberately target a normalized gateway contract rather than coupling Request Portal to Meta, Twilio, or another BSP. Commissioning therefore requires only deployment/provider work, not another Request Portal feature rewrite.

For SMS, configure the gateway endpoint and API token through Common.Secrets/OpenBao using the default secret names:

- `messaging/sms/endpoint`
- `messaging/sms/api-token`

Then set `Notifications:SmsEnabled=true` and verify that the gateway translates the normalized Common.Messaging envelope to the selected SMS provider.

For WhatsApp, configure:

- `messaging/whatsapp/endpoint`
- `messaging/whatsapp/api-token`

Then set `Notifications:WhatsAppEnabled=true` and verify the gateway/provider setup end to end. The selected WhatsApp Business provider may additionally require approved sender identity, recipient opt-in, and approved message templates for business-initiated conversations; those are provider/account commissioning concerns rather than Request Portal code.

Do not enable either mobile channel until its endpoint, credential handling, provider account, diagnostics and test delivery are verified. If a channel is disabled, users cannot enable that preference through the portal.

Email delivery uses bounded retries and a terminal dead-letter state. Staff Operations exposes dead-letter messages and audited manual replay. Mobile notifications use the same durable Common.Messaging queue, retry/idempotency infrastructure and Request Portal notification history. Verify successful delivery, preference behavior, provider failure, automatic retry, terminal dead-lettering and manual recovery for every commissioned channel.

### Do we require SMS?

No. SMS is an optional fallback channel, not a Request Portal production dependency. A production deployment is valid with email only, or with email plus WhatsApp. SMS is useful when a recipient lacks WhatsApp/data service or when FFP deliberately wants a short fallback channel, but `SmsEnabled=false` does not make the portal incomplete.

## 9. Upload security

Production requires configured size limits, extension/signature validation and malware scanning before document metadata is accepted. Commission the actual ClamAV endpoint and test clean files, EICAR in a non-production environment, scanner unavailable, oversized uploads, signature mismatch, and cross-organization access denial.

## 10. Fulfilment

Approved requests can be routed to Local ICP or FFTP US fulfilment. Production fulfilment now tracks durable line-level requested quantity, fulfilled quantity, fulfilled value and notes. Staff may record partial progress, but a request cannot transition to Fulfilled until every line quantity is complete.

Validate:

- one authoritative fulfilment per request;
- repeated Start with the same source/reference is idempotent;
- over-fulfilment is rejected atomically;
- partial quantities and values survive restart;
- completion is blocked while quantities remain;
- cancellation returns a pending request to Approved;
- a fulfilled fulfilment cannot be cancelled;
- audit entries are written for start, progress, complete and cancel.

## 11. External/local-system integration

FFP Manager integration supports durable tracking, duplicate inbound suppression, automatic polling/reconciliation, staff-triggered retry, and staff-triggered immediate reconciliation. Retry and reconciliation actions are audited.

An integration is not commissioned merely because a link can be created. If its adapter is disabled or unknown, Request Portal fails the staff retry/reconciliation action rather than pretending synchronization succeeded.

Validate external submission, provider outage, retry, external reference capture, polling, immediate reconciliation, duplicate inbound status, status mapping, and manual recovery.

## 12. Reporting

Staff reporting provides server-side filtering and paging plus CSV, true XLSX, and a print-optimized management view. The print view opens separately and supports browser **Print / Save as PDF** without truncating the filtered result set.

Validate authorization, filters, paging, large result sets, XLSX opening in Excel, and PDF output from the print surface.

## 13. Reverse proxy and HTTPS

Terminate TLS with the approved certificate and forward only trusted proxy headers. Validate HSTS, HTTPS redirect, CSP, frame denial, MIME sniffing protection, host filtering, secure cookies and rate limiting.

## 14. Monitoring and diagnostics

Commission Starfleet Diagnostics/monitoring for database, durable storage, malware scanner, OpenBao, SMTP, staff authentication, notification dead letters, integration failures and repeated fulfilment/integration errors. Add gateway/provider monitoring for each commissioned SMS or WhatsApp channel. Preserve correlation IDs through request and asynchronous processing paths.

## 15. Backup and recovery

Back up PostgreSQL, durable document storage and Data Protection keys. Perform a recovery drill into a non-production environment and verify representative access, document retrieval, fulfilment progress, notification history, integration status and audit history.

## 16. Go-live gates

Before DNS cutover:

1. CI is green, including build, tests and migration-model verification.
2. Production database is created/upgraded from the reviewed migration chain.
3. OpenBao AppRole authentication works with least privilege.
4. Durable document and Data Protection storage are writable, backed up and restored in a drill.
5. ClamAV is reachable and failure-path tested.
6. Staff authentication/authorization and at least one representative identity provider are tested.
7. SMTP is tested end to end, including dead-letter/replay behavior.
8. SMS and WhatsApp remain disabled unless their provider/gateway, secrets and end-to-end delivery have been commissioned. Neither is required for production release.
9. Organization-isolation and authorization regression tests are green.
10. Partial fulfilment and integration retry/reconciliation tests are green.
11. XLSX and Print / Save PDF reports are verified.
12. Production diagnostics/alerts are green.
13. Demo mode is confirmed disabled and the production guard is tested.
14. Backup/restore drill is complete.
