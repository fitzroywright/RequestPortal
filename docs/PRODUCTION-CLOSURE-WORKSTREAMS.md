# Production Closure Workstreams

This document defines the remaining production work for the operational areas identified during the production readiness review. Work must fail closed: an unavailable dependency must never silently weaken authorization, verification, document safety, auditing, notification delivery, fulfilment consistency, or integration consistency.

## Organization verification and renewal

Production completion requires:

- registration expiry dates to be treated as operational state, not display-only metadata;
- configurable warning windows before expiry;
- a re-verification queue for staff;
- representatives to be able to upload replacement evidence while retaining historical evidence;
- approval of replacement evidence to be audited;
- expiry to prevent new submissions when country policy requires current registration;
- suspension/reactivation/re-verification decisions to preserve reason, actor and timestamp;
- notifications for upcoming expiry, expiry, re-verification required and re-verification completed.

## Document security

The production document pipeline must retain all of these controls:

- server-side organization authorization before every upload, list and download;
- maximum request and document size enforcement;
- extension allow-list and signature/magic-byte validation;
- malware scanning before metadata is committed;
- durable storage outside the web application deployment directory;
- opaque generated storage keys rather than user-controlled paths;
- verification status, verifier, verification timestamp and notes;
- retention classification and archive state;
- denial of cross-organization access regardless of supplied URL/form/document id.

Operational policy must additionally define ClamAV availability, quarantine/rejection handling, retention periods, storage backup, restore testing and disposal.

## Audit coverage

Sensitive state changes must create business audit entries. The minimum production matrix is:

- organization register, profile change, approve, reject, suspend, reactivate and re-verification;
- membership request, approve, reject, role change and revoke;
- request create, edit, submit, resubmit, cancel, duplicate, approve, reject and more-information decision;
- document upload, verify, reject, retention change and archive;
- fulfilment create/start/update/complete/cancel and partial fulfilment;
- integration enqueue, dispatch, retry, reconcile, fail and manual replay;
- notification preference changes and administrative delivery/replay actions.

Audit records must include actor, action, entity type/id, organization id where applicable, timestamp and a non-secret detail. Secrets, passwords, access tokens and full sensitive document contents must never be placed in audit detail.

## Starfleet Diagnostics and operations

Diagnostics must cover startup dependencies and runtime failures. Production wiring must route Warning/Error/Critical events into the organization's monitoring/alerting destination and preserve correlation ids across request, outbox, fulfilment and integration processing.

Required operational signals include database, durable storage, malware scanner, OpenBao, SMTP, AD/LDAPS, external identity configuration, notification dead letters, integration dead letters and repeated fulfilment/integration failures.

## Notifications

Production delivery requires:

- user preferences enforced for optional representative notifications;
- mandatory transactional/security notices explicitly classified separately;
- retry with bounded exponential backoff;
- terminal dead-letter state after maximum attempts;
- delivery history for Sent, Failed, Skipped and DeadLettered;
- staff visibility of failed/dead-lettered deliveries;
- safe manual replay that remains idempotent;
- provider message/reference ids where available.

SMS and WhatsApp must remain unavailable unless a real provider is commissioned. The UI must not imply those channels are operational when they are not.

## Country configuration and localization

Country configuration must be managed reference data controlling registration availability, request-submission availability, verification requirements, default language, currency and country-specific classifications. Jamaica remains the initial production country unless additional countries are explicitly commissioned.

All user-facing workflow/status text intended for multi-country deployment must move to localization resources before a second language is enabled. Enabling a country must be an explicit administrative operation, not merely inserting a country code.

## Fulfilment

Fulfilment must support operationally safe repeated processing:

- one authoritative fulfilment aggregate per request unless split fulfilment is explicitly modeled;
- idempotency keys for external/manual completion commands;
- partial fulfilment and remaining quantity/value where applicable;
- source, external reference, staff actor and timestamps;
- no transition to fully Fulfilled until the completion rule is satisfied;
- audit entries for every state change;
- concurrency protection so two staff actions cannot double-complete the same work.

## External/local-system integration

No external adapter is considered production-ready until it provides:

- durable outbound/inbound envelopes;
- stable correlation and idempotency keys;
- bounded exponential retry;
- terminal dead-letter state;
- last error and attempt timestamps;
- reconciliation against the external system;
- staff visibility of pending/failed/dead-lettered messages;
- safe manual replay;
- duplicate inbound-message suppression;
- adapter-specific health/diagnostic reporting.

A production deployment may explicitly disable an uncommissioned adapter. Disabled integrations must be shown as Disabled/Not commissioned, never as healthy synchronized services.

## Management reporting

Production reporting must provide server-side search/filtering by reference, organization, country, status, category, priority and date range; summary counts; export of the filtered result; and a printable management view. Large result sets must be paged rather than silently truncated.

CSV remains useful for interoperability but does not satisfy the Excel requirement. XLSX and PDF/printable exports must be separate production outputs. Reports must use the same staff authorization boundary as the staff workspace and must not expose representative-only data to other organizations.

## Release gate

These workstreams are green only when implementation, automated regression tests, migration coverage, operational configuration and failure-path testing are complete. A screen existing in the application is not sufficient evidence of production readiness.
