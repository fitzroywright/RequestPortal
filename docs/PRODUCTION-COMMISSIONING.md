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

Expected secrets include the RequestPortal PostgreSQL connection string, commissioned external identity provider credentials, SMTP password, and any enabled integration credentials.

## 4. Durable document storage

`Storage:Local:RootPath` must point to durable external storage and not the application deployment directory. The service identity requires only the permissions needed for the Request Portal document root. Back up PostgreSQL and document storage as one recovery set and perform a restore test.

## 5. Persistent Data Protection keys

`Security:DataProtectionKeysPath` must point to durable storage shared by every Request Portal instance. Restrict it to the Request Portal service identity and backup administrators.

## 6. Staff authentication and authorization

Commission the configured staff OIDC provider and validate FFP group claims against the configured reviewer and administrator groups. Test an authorized reviewer, an authorized administrator, an ordinary FFP user, and a disabled/locked account. The authorization boundary must deny staff privileges when required claims or group membership are missing.

## 7. External representative identity

Commission at least one supported external identity provider. Store secrets in OpenBao. Test new sign-in, returning sign-in, verified-email linking, logout, callback failure, and non-local return URL rejection.

## 8. Notifications

Email is the currently commissioned production notification channel. Configure SMTP host/port/from address/username in production configuration and keep the password in OpenBao.

Production defaults explicitly set:

```json
"Notifications": {
  "Enabled": true,
  "SmsEnabled": false,
  "WhatsAppEnabled": false
}
```

SMS and WhatsApp remain visible as product concepts but cannot be enabled by the user or by a crafted request until a real provider is commissioned. Do not set either flag true merely to expose UI controls; a real sender adapter, credentials, diagnostics and end-to-end delivery test are required first.

Email delivery uses bounded retries and a terminal dead-letter state. Staff Operations exposes dead-letter messages and audited manual replay. Verify successful email, preference suppression, provider failure, automatic retry, terminal dead-lettering, and manual replay.

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

Commission Starfleet Diagnostics/monitoring for database, durable storage, malware scanner, OpenBao, SMTP, staff authentication, notification dead letters, integration failures and repeated fulfilment/integration errors. Preserve correlation IDs through request and asynchronous processing paths.

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
8. SMS/WhatsApp remain explicitly Not Commissioned unless real adapters have been commissioned.
9. Organization-isolation and authorization regression tests are green.
10. Partial fulfilment and integration retry/reconciliation tests are green.
11. XLSX and Print / Save PDF reports are verified.
12. Production diagnostics/alerts are green.
13. Demo mode is confirmed disabled and the production guard is tested.
14. Backup/restore drill is complete.
