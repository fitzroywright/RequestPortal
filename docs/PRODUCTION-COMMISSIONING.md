# Request Portal Production Commissioning

This runbook is the minimum production commissioning checklist for Request Portal.

## 1. Environment

Set `ASPNETCORE_ENVIRONMENT=Production` on the application host. Production startup intentionally fails closed if durable storage, persistent data-protection keys, OpenBao, or EF migrations are not ready.

## 2. PostgreSQL and EF Core migrations

Restore the pinned EF tool and generate/review migrations from the repository root:

```powershell
dotnet tool restore
$env:REQUESTPORTAL_MIGRATION_CONNECTION="Host=<server>;Port=5432;Database=<migration-db>;Username=<user>;Password=<password>"
dotnet ef migrations add InitialProductionBaseline --project src/RequestPortal.Infrastructure --startup-project src/RequestPortal.Web --output-dir Persistence/Migrations
dotnet ef migrations script --idempotent --project src/RequestPortal.Infrastructure --startup-project src/RequestPortal.Web -o artifacts/requestportal-migration.sql
```

Review the generated SQL before applying it. Production startup calls `Database.MigrateAsync()` and refuses to start if the assembly contains no migrations.

For an existing demo database that is being promoted, do not blindly run a generated create migration against it. Create a clean production database and migrate it, or explicitly baseline the existing schema after comparing it to the generated migration.

## 3. OpenBao / Common.Secrets

Request Portal uses `RequestPortal.Common.Secrets.ISecretProvider`. Production uses the OpenBao provider.

Configure non-secret settings in `appsettings.Production.json`:

- `OpenBao:Address`
- `OpenBao:Mount`
- `OpenBao:BasePath`
- `OpenBao:TokenEnvironmentVariable`

Set the bootstrap token only in the service environment, for example `OPENBAO_TOKEN`. Do not store it in JSON or source control.

The KV v2 paths expected under the configured base path are:

```text
ConnectionStrings/RequestPortal
Authentication/Google/ClientId
Authentication/Google/ClientSecret
Authentication/Microsoft/ClientId
Authentication/Microsoft/ClientSecret
Authentication/Facebook/AppId
Authentication/Facebook/AppSecret
Notifications/SmtpPassword
```

Each secret may be stored with a `value` property. The OpenBao policy for the Request Portal machine identity should grant read-only access only to the Request Portal subtree.

## 4. Durable document storage

`Storage:Local:RootPath` must point to durable external storage, normally an SMB path hosted by the file/NAS platform. Do not use the application deployment folder.

Example:

```text
\\fileserver\RequestPortal\Documents
```

The Windows/Linux service identity running Request Portal must have create/read/delete permissions only on the Request Portal document root. Back up the document root together with PostgreSQL and test restoration of both as one recovery set.

## 5. Persistent ASP.NET Data Protection keys

`Security:DataProtectionKeysPath` must point to a durable path shared by all Request Portal instances, for example:

```text
\\fileserver\RequestPortal\DataProtectionKeys
```

Restrict this directory to the Request Portal service identity and backup administrators. Losing these keys invalidates authentication cookies and other protected payloads.

## 6. Active Directory commissioning

Production is configured for LDAPS. Validate:

- server resolves and is reachable on TCP 636;
- certificate chain is trusted by the Request Portal host;
- `SearchBase` is correct;
- FFP reviewer and administrator security groups exist;
- a normal AD user outside those groups cannot obtain FFP staff access;
- disabled/locked accounts cannot authenticate.

Default configuration points at `JMAD01.fftpj.local`, domain `fftpj.local`, search base `DC=fftpj,DC=local`. Confirm the actual production group names before go-live.

## 7. External identity providers

Register the production Request Portal callback URLs with each provider that will be enabled. Store provider IDs/secrets in OpenBao. A provider is only activated when both required values are present.

Test new representative sign-in, returning representative sign-in, account linking by verified email, logout, callback failure, and a forged/non-local return URL.

## 8. SMTP commissioning

Set the non-secret SMTP host, port, sender address and username in production configuration. Store `Notifications:SmtpPassword` in OpenBao. Send and verify:

- organization approval;
- membership approval/rejection;
- request submission;
- more-information request;
- approval/rejection;
- notification preference suppression;
- failed-send retry/history.

## 9. Upload security

Production uploads are limited to PDF, PNG, JPEG, DOCX and XLSX and default to 25 MiB maximum. Extension and file signatures are checked before storage. Add malware scanning at the infrastructure edge before public go-live if an enterprise scanner/ClamAV service is available; scanning is still a mandatory production control even though signature validation is now implemented.

## 10. Reverse proxy and HTTPS

Terminate TLS with the approved certificate and forward `X-Forwarded-For` and `X-Forwarded-Proto`. Only trusted reverse proxies should be allowed to reach the application listener. Confirm HSTS, HTTPS redirection, CSP, frame denial, MIME sniffing protection and rate limiting.

## 11. Go-live gates

Before DNS cutover:

1. CI green.
2. Production database created by reviewed EF migrations.
3. OpenBao reachable with least-privilege machine token.
4. Durable document path and Data Protection path writable by the service identity.
5. AD and at least one external identity provider tested.
6. SMTP tested.
7. Backup and restore drill completed.
8. Organization-isolation and authorization tests green.
9. Production Starfleet Diagnostics green.
10. Demo mode confirmed disabled.
