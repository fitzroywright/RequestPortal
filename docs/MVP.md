# Request Portal MVP

Target: a credible end-to-end demonstration of the core Request Portal business process.

## Demo journey

1. Public visitor opens the FFP-branded landing page.
2. Representative signs in through an external identity provider.
3. Representative views organizations they belong to.
4. Representative either requests membership in an existing organization or registers a new organization.
5. FFP approves a pending organization.
6. The organization's administrator approves pending representatives.
7. Representative chooses the organization they are acting on behalf of.
8. Representative creates a request, adds requested items and optionally supporting documents.
9. Representative submits the request.
10. FFP sees the submitted request in a review queue.
11. FFP can request more information, approve, reject, or cancel according to policy.
12. Representative sees current status and immutable status history.

## MVP entities

- ApplicationUser
- Organization
- OrganizationMembership
- AssistanceRequest
- AssistanceRequestItem
- RequestDocument
- RequestStatusHistory
- UnitOfMeasure
- AuditEntry
- OutboxMessage
- InboxMessage

## Organization lifecycle

`PendingApproval -> Approved -> Suspended`

Rejected organizations are retained for audit purposes rather than physically deleted.

## Membership lifecycle

`Pending -> Approved -> Rejected/Revoked`

The representative who submits a new organization registration becomes its initial administrator only after FFP approves that organization. Subsequent representatives require approval by an approved organization administrator.

## Request lifecycle

`Draft -> Submitted`

From Submitted:

- `MoreInformationRequired -> Submitted`
- `Approved`
- `Rejected`
- `Cancelled`

Post-MVP fulfilment states extend Approved with `PendingFulfilment -> Fulfilled -> Closed`.

## Security invariants

- External authentication establishes identity only. Portal authorization is local and policy-based.
- A user may belong to multiple organizations.
- A request is permanently owned by the organization selected when it is created.
- A request records the user who created/submitted it.
- Every read/write operation on organization-owned data verifies active membership or an FFP staff policy.
- Organization identifiers supplied by the browser are never trusted without server-side authorization.
- Sensitive configuration and credentials are resolved through `RequestPortal.Common.Secrets`.
- No production secrets belong in appsettings files or source control.

## Persistence

PostgreSQL is the only application database for the new implementation. EF Core mappings live in Infrastructure. Migrations are owned by the Infrastructure project and applied explicitly during deployment/startup tooling rather than silently rewriting production databases.

All timestamps are UTC. Regional presentation is a UI concern.

## Engineering conventions

- .NET 10 target.
- File-scoped namespaces.
- PascalCase for types, methods, public properties, and constants.
- camelCase for fields, parameters, and local variables.
- Explicit local variable types rather than `var` unless the type cannot reasonably be written.
- Allman braces.
- Grouped usings.
- Nullable reference types enabled.
- Async I/O end-to-end.
- CancellationToken accepted by application/infrastructure operations.
- Domain rules do not depend on EF Core or ASP.NET Core.
- No direct database business logic in Razor pages/controllers.
- No in-memory persistence implementation in production code.

## Common components

The solution owns and namespaces its reusable infrastructure locally:

- `RequestPortal.Common.Security`
- `RequestPortal.Common.Messaging`
- `RequestPortal.Common.Secrets`
- `RequestPortal.Common.Diagnostics`
- `RequestPortal.Common.Storage`
- `RequestPortal.Common.Time`

These components may reuse proven implementation patterns from other internal systems, but Request Portal has no runtime or namespace dependency on those systems.
