# Integrations visual and behavioral verification

The supplied **Integrations Request Portal Dashboard.png** is the acceptance baseline. The implementation was compared with it at its native **1672 × 941** dimensions and inspected at all three requested desktop sizes. The source image and rendered evidence are retained here for review.

## Visual comparison

- [Approved mockup](integrations-visual/approved-mockup.png)
- [Rendered at 1672 × 941](integrations-visual/rendered-1672x941.png)
- [Rendered at 1648 × 928](integrations-visual/rendered-1648x928.png)
- [Rendered at 1440 × 900](integrations-visual/rendered-1440x900.png)
- [Rendered at 1920 × 1080](integrations-visual/rendered-1920x1080.png)

At the native mockup dimensions the rendered header is 50 px high, the first summary tile starts at y=151 and is 64 px high, and the grid begins at y=309. The comparable expanded panel is 209 px high. The navy header, five tinted status tiles, outlined status icons, borders, corner radii, toolbar, grid columns, compact rows and inline diagnostic columns were inspected and refined against the supplied image. Tile backgrounds and the navy header were sampled from the reference image.

The screenshots use the application's compiled Razor views, actual styles and JavaScript with **synthetic service fixtures**. No production data or external-system writes were used. Fixture names, references, diagnostic text, account name and unread counts intentionally differ from the image. Production content comes from the integration services. Longer real diagnostics may increase row/panel heights and remain inside the rows' scroll region. At narrower widths, status text and panel actions wrap to preserve access. Native date input formatting follows the browser/OS locale. This is a manual visual comparison with automated layout/interaction assertions, not a claim of pixel-identical image matching.

## Viewport constraints

The body is a viewport-height flex column. The existing shell header participates in normal flex sizing, so its actual height is reserved without a hardcoded subtraction. The title, tiles, toolbar and grid header do not scroll. Only `.integration-rows` scrolls; expanded panels and complete history have no nested scroll containers.

| Viewport | Document width × height | Vertical / horizontal overflow |
| --- | --- | --- |
| 1672 × 941 | 1672 × 941 | None / none |
| 1648 × 928 | 1648 × 928 | None / none |
| 1440 × 900 | 1440 × 900 | None / none |
| 1920 × 1080 | 1920 × 1080 | None / none |
| 1024 × 768 | 1024 × 768 | None / none |
| 768 × 900 | 768 × 900 | None / none |
| 390 × 844 | 390 × 844 | None / none |

[Measured bounds](integrations-visual/viewport-results.json) include header, title, tiles, filters, grid and expanded panel dimensions. Browser assertions cover 100 rows, scrolling to the last row, opening system diagnostics, keeping all fixed controls visible, avoiding overlapping row actions, and keeping an already-visible selection from hiding preceding rows.

## Behavior and data

- Search supports applicant names, request reference, applicant TRN, FFP Manager reference and destination ID. Date bounds retain UTC semantics; status filtering and deterministic sorting use the existing query service, with a new newest-activity sort.
- Expansion lazily retrieves the authenticated, non-cacheable diagnostic partial. View opens inline details; full history expands in the same row region. Copy uses the actual stored correlation/link ID and reports success or failure.
- Rows and panels display stored classified errors, failure stage/classification/code, operation, attempt timings, attempt count/schedule, destination reference and audit history. Unclassified legacy provider payloads remain protected. Missing diagnostics are identified as unavailable.
- Recovery instructions are taken from the existing `TransferFailures` diagnostic catalog only for recognized stored codes. Unknown codes receive no invented instructions.
- Administrator Retry/Recheck actions retain authorization, antiforgery and current filters. Retry still uses destination reconciliation, the immutable request identity and the existing request lock. Existing lost-acknowledgement, retry-idempotency and concurrent-transfer regression tests passed against an isolated local PostgreSQL test instance.
- Bridge transport failures return an actionable message and trace reference without disclosing the transport exception payload.
- Both transfer-list and attempt-history CSV links, Razor handlers and the exclusively used formatter were removed. Former handler URLs return the ordinary HTML page rather than a CSV attachment. Unrelated report exports remain covered by the full suite.
- Return-event health/history, SharePoint testing, dead letters and notification diagnostics remain accessible through the row's more-actions menu and open inside the row scroll region. An empty result includes the same system-diagnostics control.

## Validation

- Full Release solution run: **463 passed**, **10 skipped**, **0 failed** (19 domain; 444 infrastructure). The skips require separately opted-in live FFP SQL fixtures; production was not contacted.
- Final Razor/navigation tests: **19 passed**. Final changed diagnostic/page tests: **46 passed**.
- Existing staff navigation browser suite: **42 cases passed** at desktop, tablet and mobile sizes.
- Integrations browser suite: **7 viewport cases passed**, covering all requested sizes, native mockup size, 1024 × 768, 768 × 900 and 390 × 844; expansion/collapse, full details, copy, diagnostic-fetch failure/recovery, GET filter submission/clear and restricted Reviewer actions.
- Release application build succeeded without warnings or errors. A Release publish to a local temporary directory succeeded. No deployment was performed.
- The new viewport suite is included in Request Portal CI. No FFPJM schema, production records or EF migrations were changed.

To reproduce, run the Release tests with `REQUESTPORTAL_POSTGRES` pointing to an isolated test database and `REQUESTPORTAL_STAFF_NAV_RENDER_OUTPUT` pointing to an artifact directory. Run `scripts/tests/test_staff_search_navigation.cjs` and `scripts/tests/test_integrations_viewport.cjs` with the same artifact directory and `PLAYWRIGHT_MODULE` set to the installed Playwright package. `CHROME_EXECUTABLE_PATH` optionally selects an installed Chromium browser.

## Changed files

| Area | Files |
| --- | --- |
| Contracts and queries | `RequestPortal.Application/Integration/IIntegrationStatusService.cs`; `RequestPortal.Infrastructure/Integration/IntegrationMonitorQueries.cs` |
| Shell and page | `RequestPortal.Web/Pages/Shared/_Layout.cshtml`; `RequestPortal.Web/Pages/Staff/Integrations.cshtml`; `RequestPortal.Web/Pages/Staff/Integrations.cshtml.cs` |
| Grid and diagnostic views | `RequestPortal.Web/Pages/Staff/_IntegrationTransfers.cshtml`; `_IntegrationDiagnostics.cshtml`; `_IntegrationIcons.cshtml`; `_IntegrationOperations.cshtml` |
| Styling and interactions | `RequestPortal.Web/wwwroot/css/integrations.css`; `RequestPortal.Web/wwwroot/js/integrations.js` |
| Tests and CI | `RequestPortal.Tests/RequestPortal.Infrastructure.Tests/StaffSearchNavigationPageTests.cs`; `IntegrationMonitoringTests.cs`; `scripts/tests/test_staff_search_navigation.cjs`; `scripts/tests/test_integrations_viewport.cjs`; `.github/workflows/ci.yml` |
| Verification evidence | This report and `docs/integrations-visual/` reference, render screenshots and viewport measurements |
