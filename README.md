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

See `docs/MVP.md` for the demo scope and workflow.
