# Architecture

## Solution boundaries

`RequestPortal.Domain` owns business invariants and workflow.

`RequestPortal.Application` owns use cases, authorization boundaries, validation contracts, and ports.

`RequestPortal.Infrastructure` owns PostgreSQL/EF Core, identity-provider adapters, Active Directory integration, durable messaging, file storage adapters, email/notification adapters, and integrations.

`RequestPortal.Common` owns small cross-cutting abstractions that are genuinely shared inside this solution: Security, Messaging, Secrets, Diagnostics, Storage, and Time.

`RequestPortal.Web` is the public and staff web host. It must remain thin; business rules belong in Domain/Application.

`RequestPortal.Worker` handles outbox dispatch, notifications, synchronization, retention and other retryable background operations.

## Authentication and authorization

There are two deliberately separate authentication paths.

### External organization representatives

External login proves a representative's identity. It does not grant organization access.

A representative can link supported external identities to one local `ApplicationUser`. Portal authorization is based on local `OrganizationMembership` records. A person can hold memberships in multiple organizations.

The selected organization is explicit context for request creation. The server validates membership for every organization-owned operation.

### FFP internal staff

FFP staff authenticate with their FFP Active Directory credentials. Social identity providers are not accepted for staff authentication.

The application does not store or log staff passwords. Credentials are validated against the configured FFP directory over a protected directory channel. After successful directory authentication, the portal maps the directory identity to a local staff profile and portal roles/policies.

Directory authentication answers "is this a valid FFP staff identity?" Portal authorization answers "what may this staff member do in Request Portal?" The two concerns remain separate.

For deployment, LDAPS/Kerberos-integrated options may be used depending on hosting topology. Direct insecure LDAP is prohibited in production.

## Database

PostgreSQL via Npgsql/EF Core. Database naming is snake_case at the physical layer where configured. Enumerated lifecycle values are persisted as readable strings. Monetary/quantity values use explicit precision.

## Reliable messaging

State-changing operations that require asynchronous side effects write an outbox record in the same database transaction. A worker dispatches messages and marks them processed. Consumers use inbox/idempotency records where duplicate delivery would be harmful.

Initial message contracts include organization approval, request submission and request status changes.

## Secrets

Application code depends only on `ISecretProvider`. Provider implementations can resolve secrets from the approved production secret store and development-safe providers. No provider-specific SDK belongs in Domain/Application.

Directory connection settings and any service-account credentials used for directory lookup are secrets/configuration and are never committed to source control.

## Storage

Supporting documents are metadata records in PostgreSQL plus content in a storage provider. The database does not become the general-purpose document archive. Storage is accessed through `RequestPortal.Common.Storage` contracts and must support retention/audit policy.

## Diagnostics

Critical dependencies expose startup/health diagnostics through Starfleet Diagnostics. Production should fail clearly when required secrets, PostgreSQL, or required directory connectivity are unavailable, while optional integrations report degraded health without hiding the root cause.

## Migration from the prototype

The prototype is a business-reference source, not the architecture of the new system. Preserve business meaning and required data; do not reproduce insecure authorization, committed secrets, direct EF logic in pages, or provider-specific authentication coupling.
