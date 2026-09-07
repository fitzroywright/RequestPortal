# Starfleet Diagnostics

Starfleet Diagnostics is the Request Portal operational diagnostics standard.

## Principles

- Every important dependency has a named diagnostic check.
- Failures use stable diagnostic codes that can be searched in logs and support notes.
- Diagnostic events include component, severity, message, optional detail, correlation id, event id, and UTC timestamp.
- Passwords, tokens, connection strings, OAuth secrets, document contents, and other sensitive values must never be written to diagnostic detail.
- Required infrastructure fails loudly. Optional integrations report degraded state without disguising the cause.

## Initial codes

| Code | Component | Meaning |
| --- | --- | --- |
| `SF-CONFIG-DB-001` | Configuration | PostgreSQL connection string is missing |
| `SF-STARTUP-DB-001` | PostgreSQL | Database startup diagnostic failed |
| `SF-STARTUP-OK` | Startup | Named startup diagnostic passed |
| `SF-STARTUP-FAIL` | Startup | Named startup diagnostic failed |
| `SF-AUTH-AD-OK` | Security.ActiveDirectory | FFP staff authentication succeeded |
| `SF-AUTH-AD-FAIL` | Security.ActiveDirectory | FFP staff directory authentication failed |
| `SF-AUTH-STAFF-DENIED` | Security.Authorization | Valid FFP identity lacks authorized portal group membership |
| `SF-AUTH-LOCALUSER-FAIL` | Security.Identity | Local portal identity creation failed after directory authentication |

## Startup checks

The MVP performs at least these startup checks:

1. PostgreSQL connectivity.
2. FFP Active Directory endpoint reachability.
3. Required configuration/secrets as they are introduced.
4. Durable messaging store availability.
5. Document storage availability when document upload is enabled.

## Severity

- Trace: deep troubleshooting only.
- Information: successful lifecycle or dependency event worth retaining.
- Warning: degraded operation, denied authorization, retryable problem, or suspicious but non-fatal condition.
- Error: an operation failed and needs attention.
- Critical: required subsystem is unavailable or application integrity is at risk.

## Correlation

Web requests, background messages, request workflow transitions, and integration activity should carry a correlation id where possible so one user action can be followed across web, database, outbox, worker, notification, and integration logs.
