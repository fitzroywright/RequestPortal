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
4. Communication preferences and notifications: email preference, SMS placeholder, notification history and reminders.
5. Document lifecycle: retention classification, verification state, active/archive state and FFP verification metadata.
6. Fulfilment and integration: Local ICP / FFTP US routing, retry/idempotency and external reference tracking.
7. Country/language readiness: country configuration, timezone/display region, localization resources and country-specific rules.
8. Reporting/operations: organization/request dashboards, aging queue, action-required queue and audit/export views.

## Demo acceptance path

1. Representative lands on the branded opening page.
2. Representative signs in.
3. Representative sees their organizations.
4. Representative registers a new organization or requests membership in an existing one.
5. FFP staff approves the organization.
6. Organization administrator approves the representative.
7. Representative selects the organization they are acting for.
8. Representative creates a request with header data, beneficiary context, line items and supporting documents.
9. Representative submits the request.
10. FFP staff reviews the request.
11. FFP staff approves, rejects or requests more information.
12. Representative sees the new status and history on their dashboard.
