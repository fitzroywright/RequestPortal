# Applicant identity administration and recovery

Account & Identity Management is under Administration → Staff & Roles. The public
OAuth sign-in flow still refuses to associate an unbound provider with an existing
applicant merely because an email address matches. That refusal causes
`IdentityEnrollmentRequiredException`; incognito browsing cannot change the persisted
binding requirement.

## Authorization and ownership checks

The existing active staff `security.manage` requirement remains mandatory. Separate
permissions are `identity.view`, `identity.recovery.initiate`, and
`identity.recovery.approve`. Existing Administrator authorization includes them through
its established wildcard permission; Reviewer does not receive them. Staff & Roles
shows the effective permissions. The backend rechecks active staff state and effective
permissions, rather than trusting posted roles or a hidden button.

Sensitive decisions require staff authentication within ten minutes. A stale session
returns to the existing Entra login with a validated local return URL. Recovery always
requires two distinct authorized administrators. The second administrator records the
approved independent ownership-verification method and case reference and explicitly
attests that verification goes beyond matching email. This is a support-process
attestation, not automatic proof of ownership; do not approve an unverified real case.

1. Find the existing applicant by name, email, reference, or account status.
2. Initiate recovery with a support reason and configured provider.
3. A different freshly authenticated administrator verifies ownership independently
   and approves the pending recovery. Pending decisions expire after 24 hours.
4. Deliver the displayed-once invitation through that independently verified support
   channel. The invitation path is relative to this portal; use its HTTPS origin.
5. The applicant follows the invitation and authenticates with the approved provider.
6. The correlated, protected OAuth callback supplies the immutable identity. Staff
   never enter provider subjects. An identity bound elsewhere is rejected.
7. Common.Security attaches the identity to the original account, preserving request
   ownership and memberships. Legacy applicant records without Common.Security
   enrollment are enrolled with their original application account ID.
8. The existing outbox queues a notification to the registered applicant email.

Recovery only **adds** verified sign-in methods. It provides no administrator
impersonation or identity-removal facility and cannot remove a final usable provider. Self-service unlink decisions are serialized
per account with a PostgreSQL advisory transaction lock, and must leave a verified
identity whose provider is still configured, with fresh applicant authentication
using that exact remaining identity. An opaque fingerprint in the protected applicant
cookie identifies the verified binding without exposing its provider subject. Existing
sessions without that proof must sign in again before unlinking. Common.Security retains its atomic
removal, revocation tombstone, audit, and session-invalidation behavior.
A registered contact email is required for recovery security notifications; missing
contact information must go through the approved contact-verification process first.
Inactive applicants cannot be activated by recovery. Initiator and approver must still
be active and authorized when the invitation is redeemed.

## Token, audit, and failure handling

Invitations expire after 30 minutes, contain 256 random secret bits, and are stored only
as SHA-256 hashes. A fragment transports the invitation to the landing page without
sending it in the URL or Referer. Page JavaScript clears the fragment and submits the
code by an antiforgery-protected POST. OAuth state and the external cookie protect the
code during provider authentication; the callback requires the recovery-specific XSRF
binding. Responses containing invitations disable caching and referrers. The existing
Common.Security login rate limit also covers the recovery endpoint.

Atomic lifecycle transitions prevent concurrent callback replay. The invitation is
burned **before** Common.Security's independent atomic binding transaction. A failure
or process interruption cannot leave a reusable invitation: `Redeeming` indicates
operator investigation is required; `Failed` requires a newly approved invitation.
Never reset an invitation to Approved to retry a callback. Common.Security preserves
its unique immutable bindings and session invalidation. Successful recovery and its
notification outbox record commit together. Recovery lifecycle events are stored
atomically with decisions and shown in the account's audit trail.

The safe callback codes are `RP-RECOVERY-CALLBACK`, `RP-RECOVERY-INVITATION`,
`RP-RECOVERY-CORRELATION`, and `RP-RECOVERY-LINK`. Provider payloads, tokens, and subjects
are not displayed by administration. Existing `RP-AUTH-EXTERNAL-*` diagnostics remain
in the authorized diagnostics infrastructure. An unbound external callback cannot
safely be attributed to an applicant account using an email assertion alone.

## Deployment and rollback

Migration `20261104070000_ApplicantIdentityRecovery` adds only two Request Portal
PostgreSQL tables and their constraints/indexes: `requestportal_identity_recoveries`
and `requestportal_identity_recovery_events`. It does not alter existing records,
columns, identity bindings, connection targets, document storage, or integration data.
The Reports/Dashboard layout changes require no schema changes.

Before production deployment, verify the established Request Portal PostgreSQL backup
and restore arrangements with the authorized database operator. The Aegis application
and configuration backup is not a PostgreSQL data backup. Use the existing
Aegis.Operations portal-only deployment and its application/configuration backup,
health checks, and rollback safeguards; do not deploy the Bridge or Studio.

Application rollback retains the additive tables and audit trail; Down deliberately
rejects dropping this security history. Do not restore an old database over newer
applicant records. Outstanding invitations cannot be redeemed by the old application.
Drain recovery-completion notification work before a planned rollback, or preserve
and inspect it using the established outbox recovery procedure after redeployment;
the old worker does not recognize the new notification type.

Production acceptance needs an authenticated authorized staff session and a controlled,
independently verified applicant/provider account. Do not use the reported real
applicant for a demonstration without verified ownership and authorization. Automated
OAuth tests use a synthetic provider only inside TestServer; they are not evidence of
an actual production Google sign-in.
