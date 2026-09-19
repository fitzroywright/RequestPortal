# Request Portal Operational Runbook

## Purpose
Provides organization/request intake, supporting documents, staff workflows and downstream FFP Manager integration.

## Dependencies
Database, Common.Storage, authentication/identity provider, Common.Messaging, Aegis.Configuration/Registration, and optional FFP Manager integration.

## Startup
Verify database connectivity, storage health, registration, required configuration and staff-authentication configuration.

## Shutdown
Stop normally. Preserve database outbox, documents, registration identity and messaging queues.

## Health Verification
Inspect startup diagnostics, staff-authentication traces, request-submission flows, document/storage diagnostics and outbox state.

## Normal State
Fresh diagnostics; staff authentication completes through Session; submitted requests progress through durable outbox; documents persist with metadata/integrity; downstream integration is either healthy or explicitly disabled.

## Common Warnings
Optional notification channels unconfigured, transient downstream retry, or stale diagnostic evidence.

## Common Failures
Database unavailable, document storage failure, repeated authentication callback/authorization failures, dead-lettered outbox work, or downstream submission exhaustion.

## Recovery
Preserve durable outbox/document state. Restore the dependency, then verify fresh correlated flow evidence and successful retry.

## Backup / Restore
Back up database and document storage together. Restore must preserve document metadata/storage correspondence and outbox identity.

## Deployment / Rollback
Do not delete outbox data, document storage or registration identity during deployment or rollback.

## Purge / Re-registration
Use explicit administrative procedures only. Purge must not substitute for registration recovery.

## Escalation
Escalate repeated staff-authentication failures, document-integrity failures, dead-letter growth or FFP Manager handoff failure.

## Log Locations
Application/service logs plus Operations/Diagnostics correlation IDs. Never log OIDC codes/tokens, passwords, session tokens, registration credentials or document contents.
