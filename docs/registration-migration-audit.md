# RequestPortal registration migration audit

Branch: `common-registration-migration`

## Scope

This audit covers RequestPortal configuration contract publication. It does not change Aegis.Operations, Aegis.Configuration, or Aegis.Diagnostics product behavior.

## Registration boundary

- RequestPortal registers only with Aegis.Configuration.
- Registration transport is provided by Common.Registration.
- RequestPortal does not register with Aegis.Diagnostics.

## Configuration ownership rule

Aegis.Configuration describes and monitors what RequestPortal requires and whether RequestPortal reports those requirements configured. It does not own RequestPortal's configuration values.

RequestPortal may publish requirement metadata and configured-state evidence, including requirement names/keys, required/optional status, sensitivity, purpose, and `isConfigured` state.

RequestPortal must not publish actual values from configuration, environment variables, connection strings, OpenBao, SMTP credentials, OIDC client secrets, storage paths as display values, or any resolved/default configuration content.

The migration removes RequestPortal's previous construction of `safeDisplayValue`. Common.Registration also applies the metadata-only sanitizer centrally before transmission.

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

Common.Registration's current staging branch has executable tests for metadata-only sanitization, source-contract non-mutation, and rejected-registration failure semantics, with green restore/build/test CI evidence.

RequestPortal's current GitHub Actions job is not compilation evidence because it failed before any workflow steps executed and no runner was assigned. This branch therefore does not claim a successful RequestPortal compile/test run yet.

## Temporary repository layout

Common.Registration is temporarily staged on the `common-registration` branch of `Common.Diagnostics`. The intended permanent state is a standalone `Common.Registration` repository. RequestPortal must be restored, built, and tested against that standalone repository before this migration is promoted from draft.