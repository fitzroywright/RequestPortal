# Management dashboard geography and charts

The dashboard keeps the existing authenticated horizontal application shell and uses the existing `IManagementDashboardService` snapshot. Reporting scopes, UTC period boundaries, lifecycle timings, attention cutoffs, delivery states, and category/program definitions remain unchanged.

## Geographic data and licensing

- Publisher: William & Mary geoLab, **geoBoundaries gbOpen**.
- Dataset: Jamaica ADM1, boundary ID `JAM-ADM1-11528761`, 14 parishes, represented year 2011, build December 12, 2023.
- Upstream source: OpenStreetMap / Wambacher. geoBoundaries declares **Creative Commons Attribution-ShareAlike 2.0** for this particular dataset (do not assume all gbOpen boundaries have the same licence).
- Metadata: https://www.geoboundaries.org/api/current/gbOpen/JAM/ADM1/
- Pinned release: https://github.com/wmgeolab/geoBoundaries/tree/9469f09/releaseData/gbOpen/JAM/ADM1
- Original GeoJSON (Git LFS object): https://media.githubusercontent.com/media/wmgeolab/geoBoundaries/9469f09/releaseData/gbOpen/JAM/ADM1/geoBoundaries-JAM-ADM1.geojson
- Original SHA-256: `54e50f3b788cfcabe8d7b17031bc103c7dd550082da4755f9de6617b82d8d5b0`.
- Licence: https://creativecommons.org/licenses/by-sa/2.0/ (legal code: https://creativecommons.org/licenses/by-sa/2.0/legalcode).

`wwwroot/data/jamaica-parishes.528b7394d3b3.geojson` is the derived, share-alike geographic asset: WGS84 coordinates rounded to five decimal places, whitespace and unused metadata removed, `shapeName` converted to canonical `name`, and Saint names aligned with Request Portal's St. names. Polygon topology and all fourteen parish features are retained; no manually drawn geometry or synthetic locations are used. The filename contains the derived content hash. This geographic asset is distributed under the source's CC BY-SA 2.0 terms; the licence does not change the application's code licence. Attribution and licence links remain visible on the map.

## Local libraries and tiles

- Leaflet **1.9.4**, BSD-2-Clause, https://github.com/Leaflet/Leaflet/releases/tag/v1.9.4. Local release JS/CSS and licence are in `wwwroot/lib/leaflet/1.9.4`.
- Chart.js **4.6.1**, MIT, https://www.npmjs.com/package/chart.js/v/4.6.1 and https://github.com/chartjs/Chart.js. Local UMD distribution and licence are in `wwwroot/lib/chart.js/4.6.1`.
- Packages were fetched with `npm pack --ignore-scripts`. Release copyright notices are retained; references to non-shipped development source maps are omitted. No production CDN JavaScript dependency is required.
- Basemap: standard OpenStreetMap raster tiles, https://tile.openstreetmap.org/{z}/{x}/{y}.png. Visible attribution links to https://www.openstreetmap.org/copyright. Browser caching and the existing strict-origin-when-cross-origin referrer policy are preserved. No prefetch, geocoding, tile proxy, or offline downloader is introduced. Provider policy: https://operations.osmfoundation.org/policies/tiles/.

## Aggregation, privacy and behavior

`JamaicaParishes.Normalize` unifies Saint/St. aliases, case, whitespace and a trailing Parish label. Every dashboard includes all fourteen parish aggregates plus an explicit **Unknown / not provided** bucket for missing/unrecognized parishes. Counts reconcile with the same non-draft/non-cancelled/non-duplicate scope as the existing status metrics. Outstanding and completed retain the existing status sets. The same canonical identity is used for request-list filtering.

Only parish names and aggregate total/outstanding/completed counts, plus aggregated chart series, are embedded in the authorized management page. There is no new map-data API, applicant coordinate payload, or request-level data download. The existing ManagementDashboard permission policy still protects the page and drill-down routes. Public, immutable GeoJSON contains geography only.

Map selection opens the existing `/Management/Requests?parish=...` listing. The labelled native parish selector, View requests button, clear selection, reset control and focusable geographic boundaries offer keyboard alternatives. Street-tile failure leaves the real parish boundaries usable and reports the failure; geography/library failure shows an error and retains the parish filter. No invented map fallback is displayed.

Map and chart scripts load on approach to the viewport; shared script promises avoid duplicate loads. Geographic fetches time out, static versioned assets have immutable caching, resize preserves the full country fit, and page unload disposes the map, charts, observers and pending geographic requests. BFCache navigation keeps a live page intact.

Delivery summary counts use one aggregate SQL query instead of five. Existing location-storage reads are bounded to eight concurrent operations; they do not geocode remotely. All other existing metric calculations are preserved.


## Focused validation

`ManagementDashboardTests` reconciles persisted PostgreSQL request, status, parish, delivery and trend scopes, canonical aliases, unknown values and the pinned geographic dataset. Use a disposable test PostgreSQL connection via `REQUESTPORTAL_POSTGRES`; never point tests at production.

`ManagementDashboardPageTests` renders the real Razor pages against synthetic services and checks backend authorization, aggregate-only payloads, preserved sections, links, zero delivery data and long names. It needs no database. Set `REQUESTPORTAL_DASHBOARD_RENDER_OUTPUT` to a temporary directory when running this class to export its full, zero-delivery and long-name pages.

Run `node scripts/tests/test_management_dashboard.cjs` with that same output variable and Playwright 1.64.1 available (`PLAYWRIGHT_MODULE` may identify an existing installation). `CHROME_EXECUTABLE_PATH` optionally selects an installed Chrome executable; otherwise Playwright's Chromium is used. The script runs 21 responsive/data/console scenarios and tests keyboard/parish filtering, pan/zoom/reset and asset failure recovery. All automated interactions use test-only tiles and make no external tile requests. The browser fixtures stub the unrelated notification transport. `DASHBOARD_BROWSER_OUTPUT` optionally redirects screenshots. Visual inspection of an initial real-tile viewport is separate from the offline regression matrix.
