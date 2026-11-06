# Administrator-controlled WhatsApp test

The administrator page `/Administration/WhatsAppCommissioning` uses the existing
Common.Messaging `WhatsAppMessageChannel`, template serializer and Common.Secrets
resolver. It does not register a WhatsApp channel in the shared notification queue.
`Notifications:WhatsAppEnabled` must remain false; the inbound webhook must remain
enabled. Only the existing Meta test sender Phone Number ID `1427917097069650`,
WABA `1823610105620506` and secret reference `messaging/whatsapp/api-token` are
accepted. The Graph version comes from the effective production configuration.
No credential, general outbound flag, webhook handler or schema migration changes
are part of this feature.

## Readiness and explicit authorization

An active staff administrator with `security.manage` opens **Admin → WhatsApp test**.
Every action uses the existing StaffAdministration policy and rechecks the active
account/current administrator grants. Razor Pages antiforgery protection applies.

1. Check readiness. The protected token is resolved in memory. Read-only Graph GETs
   verify the configured phone is a verified Cloud API sender, the token reports
   `whatsapp_business_messaging` granted, and `hello_world / en_US` is approved in
   the configured WABA. Failure shows only its stage, HTTP status and numeric Meta
   error/subcode. It never sends a message. Template inspection also needs the
   applicable WABA management access. A failed permissions inspection means readiness
   is unverified; the UI never treats a readable token as proof of permission.
2. Separately verify the intended recipient in Meta's API Setup test-recipient list.
   Enter its E.164 number twice and explicitly confirm verification and recipient
   authorization. The service encrypts the recipient using the existing Data
   Protection key ring and a purpose bound to the operation ID. Authorization expires
   after ten minutes. This step never sends. Meta's test sender additionally enforces
   its verified-recipient restrictions; no recipient is automatically enrolled.
3. Only after the recipient and actual send are explicitly authorized, type
   `SEND HELLO_WORLD` for that operation and submit its one-time send action.
   Recipient, template and sender cannot be changed by this action. Readiness is
   checked again. Only the protected administrator POST initiates a test; the read-only CLI and background workers never do.
4. Refresh the same operation to inspect safe results and delivery-status receipts.
   No automatic send occurs on GET or refresh. The UI never prints recipients,
   raw message IDs, message content, bearer tokens or raw Graph response/error bodies.

No live test is authorized merely by deploying or visiting this facility. The first
controlled send still requires the user's explicit authorization of the exact
verified recipient and send. General applicant notifications remain disabled.

## Persistence, correlation and duplicate prevention

The existing `outbox_messages` table holds processed, terminal audit records of type
`RequestPortal.WhatsAppCommissioning.v1`. They are never outbound queue work items.
The existing `audit_entries` records the actor, operation and authorization/attempt/
result transitions. No table or EF migration is introduced.

A PostgreSQL compare-and-set changes Authorized to Attempted and commits its audit
before any outbound POST. Only one caller can claim the same operation across app
instances. The operation is bound to its authorizing administrator, sender settings
and expiration. Browser retries, concurrent submissions and process restarts cannot
reuse an attempted authorization. There is no automatic retry.

The existing shared channel generates the approved template request. Its local
transport adapter omits the serializer's optional null template-components member,
uses a no-redirect/no-logging HttpClient, captures Meta's response ID, and replaces
response bodies before the shared channel can put them in exception messages.
Only safe numeric Graph codes and HTTP status are retained from error responses.

Accepted means Meta returned a valid `wamid` for the request; it is not proof of
handset delivery. Rejected, NotSent and Unknown are separate states. A network timeout,
malformed success or crash after the durable Attempted transition must not be blindly
retried: Meta may already have accepted the message. If completion persistence fails,
the record deliberately stays Attempted and remains non-retryable.

The existing, unchanged signature-verified webhook stores sent/delivered/read/failed
receipts. The test status joins those processed inbox/outbox receipts by Meta message
ID and the configured WABA/phone identifiers. Receipt retries remain deduplicated by
the existing webhook store. A delivered/read receipt (and the authorized recipient's
confirmation) establishes the delivery result; API acceptance alone does not.

## Read-only CLI inspection

`RequestPortal.Web.dll inspect-whatsapp-outbound` runs the same readiness checks
under the existing protected Production environment and Common.Secrets bootstrap.
It never starts the web host, migrations, accounts initialization, workers or sends.
It preserves bootstrap SecretId files and outputs sanitized JSON. It needs the existing
workload identity and service environment; do not export tokens or copy credentials
into command arguments. Interactive sudo may be required to reproduce that context.

## Deployment safeguards and tests

Use only Aegis.Operations `scripts/Deploy-Kratos.ps1 -Applications requestportal
-ExistingDeployerOnly`, with the existing root deployer checksum matched and CommonRoot
pinned to `security-baseline.json`. Do not use the top-level Request Portal wrapper,
which also deploys the Bridge. The portal-only path preserves environment and
registration state, takes backups and performs health checks/rollback. Preserve the
external Data Protection, storage and messaging paths. No migrations, inbound handler,
nginx, secrets or authentication implementation changes are required here. Stop if
the deployment baseline unexpectedly introduces pending/destructive migrations.

Focused tests cover the shared template path, safe Graph errors and uncertain responses,
missing permissions/template, authorization/CSRF, explicit recipient consent, encrypted
recipient storage, expiration/actor binding, concurrent claims, lost completion, and
correlation/deduplication through the existing webhook receipt journal. PostgreSQL
regressions use an isolated local database; all Meta HTTP calls in tests are fakes.
