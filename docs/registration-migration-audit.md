# RequestPortal registration migration audit

Branch: `common-registration-migration`

## Scope

This audit covers RequestPortal configuration contract publication. It does not change Aegis.Operations, Aegis.Configuration, or Aegis.Diagnostics product behavior.

## Registration boundary

- RequestPortal registers only with Aegis.Configuration.
- Registration transport is provided by the standalone Common.Registration library.
- RequestPortal does not register with Aegis.Diagnostics.
- Retry occurs only while registration is unsuccessful; a real successful registration ends the startup retry cycle.

## Configuration ownership rule

Aegis.Configuration describes and monitors what RequestPortal requires and whether RequestPortal reports those requirements configured. It does not own RequestPortal's configuration values.

RequestPortal may publish requirement metadata and configured-state evidence, including requirement names/keys, required/optional status, sensitivity, purpose, and `isConfigured` state.

RequestPortal must not publish actual values from configuration, environment variables, connection strings, OpenBao, SMTP credentials, OIDC client secrets, storage paths as display values, or any resolved/default configuration content.

The migration removes RequestPortal's previous construction of `safeDisplayValue`. Common.Registration recursively removes value-bearing fields again before transmission and normalizes requirement-kind wire metadata for Aegis.Configuration.

## Runtime checks retained

RequestPortal still determines configured-state evidence for its real dependencies, including:

- PostgreSQL / RequestPortal database
- persistent storage
- data-protection key storage
- OpenBao address/authentication material
- staff OIDC authority/client configuration and secret presence
- Active Directory reviewer/administrator groups
- malware scanning
- SMTP host/from/password presence
- durable messaging queue
- site identity

Those checks answer whether requirements appear configured; they do not transfer their values to Aegis.Configuration.

## Failure semantics

Publication is successful only after Aegis.Configuration accepts the request.

Missing contract, missing registration credential, malformed contract, non-success HTTP response, timeout, and transport failure are all failures. RequestPortal remains operational and retries rather than fabricating successful registration.

## Validation evidence

The permanent `fitzroywright/Common.Registration` repository has green CI with executable tests for recursive metadata sanitization, source non-mutation, wire-kind normalization, sanitized payload transmission, HTTP rejection, timeout, and transport failure.

RequestPortal's workflow and solution consume the standalone library directly at `Common/Common.Registration`. RequestPortal's current GitHub Actions job is not compilation evidence because it failed before any workflow steps executed and no runner was assigned. This branch therefore does not claim a successful RequestPortal compile/test run yet.