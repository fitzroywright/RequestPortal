# Request Portal Requirements Gap Plan

This document tracks implementation work against the original Request Portal requirements and the clarified operating rules.

## Core operating rules

- External representatives authenticate as people through approved external identity providers.
- FFP internal staff authenticate with FFP Active Directory credentials.
- FFP approves organizations.
- Approved organization administrators approve representatives.
- A user may belong to multiple organizations.
- Every request belongs to exactly one organization.
- Representatives may only access information for organizations where they have an approved membership.
- FFP staff may access all organizations and their Request Portal information, subject to staff role authorization.
- A request may be a goods/services request or a project request.
- Project requests support a long-form rich proposal with headings, lists, tables, links and photos in addition to ordinary supporting documents.

## Implemented or substantially implemented

- PostgreSQL persistence via EF Core/Npgsql.
- ASP.NET Core Identity for local portal identity mapping.
- External representative authentication adapters.
- FFP Active Directory staff authentication boundary.
- Organization registration and FFP approval workflow.
- Organization membership request and organization-admin approval workflow.
- Multi-organization membership.
- Organization-scoped authorization checks.
- Assistance request creation, line items and submission.
- Project request type and rich narrative proposal editor.
- Project proposal version metadata and FFP review rendering.
- Request review queue for FFP staff.
- Request approve/reject/request-more-information workflow.
- Requestor dashboard and request status history.
- Supporting-document domain/storage foundation.
- Business audit foundation.
- Durable outbox/inbox messaging foundation.
- Fulfilment and integration status foundations.
- Starfleet Diagnostics.

## Next implementation priorities

1. Organization verification package: registration certificate, expiry, verification notes, rejection/suspension/reactivation.
2. Representative governance: approve/reject/revoke membership, administrator assignment/removal, last-administrator safeguard.
3. Request intake completeness: beneficiary, category/subcategory/program, priority, circumstances and location.
4. Project proposal hardening: production-grade HTML sanitization, image externalization, autosave, print/PDF and richer tables.
5. Communication preferences and notifications: email preference, SMS placeholder, notification history and reminders.
6. Document lifecycle: retention classification, verification state, active/archive state and FFP verification metadata.
7. Fulfilment and integration: Local ICP / FFTP US routing, retry/idempotency and external reference tracking.
8. Country/language readiness: country configuration, timezone/display region, localization resources and country-specific rules.
9. Reporting/operations: organization/request dashboards, aging queue, action-required queue and audit/export views.

## Demo acceptance path

1. Representative lands on the branded opening page.
2. Representative signs in.
3. Representative sees their organizations.
4. Representative registers a new organization or requests membership in an existing one.
5. FFP staff approves the organization.
6. Organization administrator approves the representative.
7. Representative selects the organization they are acting for.
8. Representative chooses goods/services or project request.
9. Goods/services requests use line items; project requests use the rich proposal editor with formatted narrative, tables and photos.
10. Representative attaches supporting documents where needed.
11. Representative submits the request.
12. FFP staff reviews the request and, for a project, reads the proposal in the review workspace.
13. FFP staff approves, rejects or requests more information.
14. Representative sees the new status and history on their dashboard and can revise the project proposal when more information is required.
