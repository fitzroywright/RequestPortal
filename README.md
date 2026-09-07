# Request Portal

Request Portal is a public-facing application for organizations to register with Food For The Poor Jamaica, manage approved representatives, and submit assistance requests on behalf of approved organizations.

The solution is designed around PostgreSQL, ASP.NET Core, external identity providers, organization membership approval, request workflow, auditing, diagnostics, secrets abstraction, and reliable messaging patterns.

## Core rules

- Organizations are approved by FFP before they can submit requests.
- Organization representatives authenticate as individual users.
- Organizations approve their own representatives.
- A user may belong to multiple organizations.
- Every request belongs to exactly one organization and records the submitting user.
- All organization-owned operations are authorized against active membership.
- Date/time values are stored in UTC and presented in the user's configured region.
- Secrets are never committed to source control.

## Quick development startup

Prerequisites: Visual Studio 2022 with .NET 10 support and access to an existing PostgreSQL server.

1. Pull the `chatgpt/mvp-rescue` branch.
2. Open `RequestPortal.sln` in Visual Studio.
3. Set `RequestPortal.Web` as the startup project if Visual Studio does not select it automatically.
4. Configure the PostgreSQL connection string using Visual Studio User Secrets for the `RequestPortal.Web` project. Use the key `ConnectionStrings:RequestPortal`.
5. Press F5.

Example connection string shape:

`Host=<postgres-server>;Port=5432;Database=requestportal;Username=<user>;Password=<password>`

Development mode enables the demo personas. `Demo:EnsureDatabase` is currently enabled for the MVP/demo path, so the application can create the schema in the configured PostgreSQL database if the account has sufficient rights.

Do not commit PostgreSQL passwords, OAuth credentials, AD settings, SMTP credentials, or other secrets. Supply them through User Secrets during development and the approved Request Portal secrets provider in production.

See `docs/MVP.md` for the demo scope and workflow.
