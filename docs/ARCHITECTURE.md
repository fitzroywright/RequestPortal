# Architecture

## Solution boundaries

`RequestPortal.Domain` owns business invariants and workflow.

`RequestPortal.Application` owns use cases, authorization boundaries, validation contracts, and ports.

`RequestPortal.Infrastructure` owns PostgreSQL/EF Core, identity-provider adapters, durable messaging, file storage adapters, email/notification adapters, and integrations.

`RequestPortal.Common` owns small cross-cutting abstractions that are genuinely shared inside this solution: Security, Messaging, Secrets, Diagnostics, Storage, and Time.

`RequestPortal.Web` is the public and staff web host. It must remain thin; business rules belong in Domain/Application.

`RequestPortal.Worker` handles outbox dispatch, notifications, synchronization, retention and other retryable background operations.

## Authentication and authorization

External login proves a person's identity. It does not grant organization access.

Portal authorization is based on the local ApplicationUser and OrganizationMembership records. A person can link multiple external identities to one local account and can hold memberships in multiple organizations.

The selected organization is explicit context for request creation. The server validates membership for every organization-owned operation.

## Database

PostgreSQL via Npgsql/EF Core. Database naming is snake_case at the physical layer where configured. Enumerated lifecycle values are persisted as readable strings. Monetary/quantity values use explicit precision.

## Reliable messaging

State-changing operations that require asynchronous side effects write an outbox record in the same database transaction. A worker dispatches messages and marks them processed. Consumers use inbox/idempotency records where duplicate delivery would be harmful.

Initial message contracts include organization approval, request submission and request status changes.

## Secrets

Application code depends only on `ISecretProvider`. Provider implementations can resolve secrets from the approved production secret store and development-safe providers. No provider-specific SDK belongs in Domain/Application.

## Storage

Supporting documents are metadata records in PostgreSQL plus content in a storage provider. The database does not become the general-purpose document archive. Storage is accessed through `RequestPortal.Common.Storage` contracts and must support retention/audit policy.

## Diagnostics

Critical dependencies expose startup/health diagnostics. Production should fail clearly when required secrets or database connectivity are unavailable, while optional integrations report degraded health without hiding the root cause.

## Migration from the prototype

The prototype is a business-reference source, not the architecture of the new system. Preserve business meaning and required data; do not reproduce insecure authorization, committed secrets, direct EF logic in pages, or provider-specific authentication coupling.
