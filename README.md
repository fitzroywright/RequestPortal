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

## Quick local demo

Prerequisites: Visual Studio 2022 with .NET 10 support and Docker Desktop.

1. Pull the `chatgpt/mvp-rescue` branch.
2. From the repository root, start the development PostgreSQL container:

   `docker compose up -d`

3. Open `RequestPortal.sln` in Visual Studio.
4. Set `RequestPortal.Web` as the startup project if Visual Studio does not select it automatically.
5. Press F5.

Development mode enables the demo personas and creates the PostgreSQL schema/data automatically. The local development PostgreSQL container listens only on `127.0.0.1:5432` and uses trust authentication solely for the local demo environment.

Production connection strings, OAuth credentials, AD settings, SMTP credentials, and other secrets must be supplied through approved configuration/secret providers and must not be committed.

See `docs/MVP.md` for the demo scope and workflow.
