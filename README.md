# Request Portal

Request Portal is a public-facing application for organizations to register with Food For The Poor Jamaica, manage approved representatives, and submit assistance requests on behalf of approved organizations.

The solution is designed around PostgreSQL, ASP.NET Core, external identity providers, organization membership approval, request workflow, auditing, diagnostics, secrets abstraction, durable storage, and reliable messaging.

## Repository policy

`main` is the authoritative trunk. Development branches are temporary integration surfaces and should not become competing long-lived product variants.

## Core rules

- Organizations are approved by FFP before they can submit requests.
- Organization representatives authenticate as individual users.
- Organizations approve their own representatives.
- A user may belong to multiple organizations.
- Every request belongs to exactly one organization and records the submitting user.
- All organization-owned operations are authorized against active membership.
- Date/time values are stored in UTC and presented in the user's configured region.
- Secrets are never committed to source control.

## Common platform integration

Request Portal consumes the shared Common libraries at the host/infrastructure boundary while retaining Request Portal domain ownership:

- `Common.Diagnostics` supplies reusable operational and engineering diagnostic mechanics.
- `Common.Security` supplies reusable staff authentication/authorization mechanics; Request Portal owns its roles, organization policy and authorization persistence.
- `Common.Secrets` supplies provider composition and production secret retrieval.
- `Common.Storage` stores uploaded document bytes and storage metadata; Request Portal retains document authorization, malware policy and business retention rules.
- `Common.Messaging` supplies durable queued external delivery; the Request Portal outbox remains the application transaction boundary.

These libraries do not own Request Portal business concepts.

## Configuration and diagnostics boundary

Request Portal publishes `configuration/RequestPortal.configuration-contract.json` as its commissioning declaration. The configuration control plane may use that contract to answer whether required settings and secret references are present and commissionable.

Runtime diagnosis remains separate. Request Portal exposes Common.Diagnostics engineering endpoints and application-specific runtime checks for database, directory, storage and other operational dependencies.

The portal deliberately does not implement configuration risk scoring, blast-radius analysis, incident command, operational briefing or remediation prioritization. Those are operational orchestration concerns, not Request Portal configuration responsibilities.

## Production commissioning

The production host still fails closed when required production settings are unsafe or absent. The configuration contract is the declarative commissioning source; startup validation remains a last-line safety guard rather than a second configuration product.

Important production requirements include PostgreSQL credentials, durable document storage, external Data Protection keys, Common.Secrets/OpenBao bootstrap configuration, staff directory settings, malware scanning, at least one representative identity provider, SMTP notifications and durable Common.Messaging queue storage.

## Quick development startup

Prerequisites: Visual Studio 2022 with .NET 10 support and access to an existing PostgreSQL server.

1. Pull `main` or the branch being reviewed.
2. Open `RequestPortal.sln` in Visual Studio.
3. Set `RequestPortal.Web` as the startup project if Visual Studio does not select it automatically.
4. Configure the PostgreSQL connection string using Visual Studio User Secrets for the `RequestPortal.Web` project. Use the key `ConnectionStrings:RequestPortal`.
5. Press F5.

Example connection string shape:

`Host=<postgres-server>;Port=5432;Database=requestportal;Username=<user>;Password=<password>`

Development mode can enable demo personas. `Demo:EnsureDatabase` is for the demo/development path only and is not a production deployment mechanism.

Do not commit PostgreSQL passwords, OAuth credentials, directory credentials, SMTP credentials, diagnostic machine credentials, or other secrets. Supply them through User Secrets during development and Common.Secrets in production.

See `docs/MVP.md` for the demo scope and workflow.
