# Engineering Diagnostics

RequestPortal exposes the Common.Diagnostics Engineering Diagnostics v1 contract used by Aegis.Diagnostics.

## Levels

- Level 5 — Quick Diagnostics
- Level 4 — Component Diagnostics
- Level 3 — System Diagnostics
- Level 2 — Integration Diagnostics
- Level 1 — Full System Diagnostics

Levels are cumulative. Level 1 is the deepest run.

## Endpoints

- `GET /health`
- `POST /api/engineering/diagnostics/run`
- `GET /api/engineering/diagnostics/runs`
- `GET /api/engineering/diagnostics/runs/{runId}`
- `POST /api/engineering/diagnostics/runs/{runId}/resolve`

## Machine authentication

Aegis.Diagnostics sends `X-Aegis-Diagnostics-Key`. RequestPortal compares it in fixed time to the value in the `REQUESTPORTAL_DIAGNOSTICS_KEY` process environment variable. The key is not stored in appsettings and must not be logged.

An already authenticated operator may also access the diagnostic API through the normal RequestPortal authentication pipeline.

## Current platform coverage

RequestPortal uses Common.Diagnostics for the Engineering Diagnostics engine. Its Level 4 and Level 2 results explicitly report that Security, Secrets, Messaging and Storage are still implemented by RequestPortal-local components until those are migrated to the central Common libraries.
