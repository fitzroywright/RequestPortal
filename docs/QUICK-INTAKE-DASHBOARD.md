# Quick Intake Dashboard

Routes (existing Management Dashboard and Request Reports routes are unchanged):

- `/Management/QuickIntakeDashboard`: staff dashboard.
- `/Management/QuickIntakeDashboard?handler=Data`: aggregate JSON, same authorization and filters.
- `/Management/QuickIntakes?From=yyyy-MM-dd&Through=yyyy-MM-dd&Parish=Kingston`: paginated, Quick Intake-only record links. Parish is optional.

The Reports dropdown contains Quick Intake Dashboard, Management Dashboard (`/Management`), and Request Reports (`/Staff/Reports`), in that order. Management destinations require the existing `ManagementDashboard` policy (staff authentication, staff surface, `ManagementDashboard.View`). The existing Request Reports authorization and functionality remain unchanged. Staff lacking management permission do not see the two management destinations. Page, data handler, and drilldown all enforce server authorization and no-store responses.

## Visual contract

Reference: `~/Downloads/a_detailed_quick_intake_dashboard_ui_mockup_for.png`. The six metrics, map/type row, household/employment/income row, and gender/age/insights row follow the approved content-area composition. Request Portal's shell and existing Reports/Admin dropdown styling are retained; no sidebar is introduced. The new page uses the same pinned Leaflet 1.9.4, Chart.js 4.6.1 and exact `jamaica-parishes.528b7394d3b3.geojson` geometry as Management. Its separate initializer/styles provide the blue graduated map and charts without changing Management's assets or behavior. The map's real geographic proportions are preserved. Legend intervals are disjoint: 0, 1–2, 3–5, 6–9, 10+.

## Data definitions

All values use the same cohort: persisted `quick_intakes.CreatedUtc` within inclusive UTC dates, implemented as `[start midnight, day after end midnight)`. Default: today and the previous 29 UTC days. Presets: 7/30/90 days; custom ranges support up to 366 inclusive days. All aggregation queries run in one read-only repeatable-read PostgreSQL transaction; no individual applicant demographics are loaded into the browser or materialized by the aggregate service.

| Display | Authoritative source / mapping |
| --- | --- |
| Quick Intakes | Count of persisted QuickIntakeRecord rows, never all assistance requests |
| Parish | Intake snapshot `Parish`, normalized by existing JamaicaParishes; all 14 parishes plus explicit unknown/invalid count |
| Request type | `HelpCategory`: Food, Housing, Earn an Income, Education, Health, Water & Hygiene; unrecognized/missing → Not provided / unknown |
| Employment | Intake snapshot `EmploymentStatus`: Employed, Self Employed, Unemployed, Student; other/missing → unknown |
| Monthly Income (JMD) | Intake snapshot `MonthlyIncome`. Current Quick Intake handlers convert the weekly form amount ×52÷12, rounded to 2 decimals, before persistence. Older captured monthly amounts are already monthly. Do not convert the stored amount again. Bins: zero, (0,50000], (50000,100000], (100000,200000], >200000; negative/unavailable → unknown |
| Gender | Intake snapshot Male, Female, Prefer not to Say; other/missing → unknown; do not reinterpret a legacy Non-Binary default as an answer |
| Age | `DateOfBirth` at intake `CreatedUtc` (UTC date), accounting for whether the birthday occurred. Groups: <25, 25–34, 35–44, 45–54, 55–64, 65+. Placeholder DOB 1900-01-01, default DOB, future DOB or age >120 → unknown |
| Sent to FFP Manager | Intake request has a link for the configured integration system, Status=Synchronized, non-null VerifiedUtc and SynchronizedUtc, nonempty external ID and application number. Queued, failed, other-system and unverified legacy sync records do not qualify. Outcome is the currently recorded state of the selected intake cohort, not a count of sync events during the date range |

The ExpandQuickIntake migration filled older rows with invented defaults (Food, Unemployed, zero income, Non-Binary, DOB 1900-01-01, address Not captured). Rows bearing that paired DOB/address sentinel are treated as unknown for the affected classifications rather than genuine answers. No rows or historical defaults are rewritten.

## Unavailable metrics

- Awaiting Screening, Follow-up Required, Screening Complete: no authoritative Quick Intake screening lifecycle is persisted. Request submission/review statuses are not substituted for screening states.
- New Applicants: anonymous tracking identities, signed-in applicants and conversions do not establish that a registered account was created because of a specific intake. No reliable immutable registration-origin link is recorded.
- Household Size: not captured on the intake. Later registered-profile values are not an intake snapshot and cannot reliably reconstruct it.

Populating these areas requires a separately approved domain change (screening states, registration attribution, historical household snapshots). This dashboard introduces no new workflow or schema.

## Privacy and statistical rules

All percentages use all Quick Intakes in the selected range, including an explicit unknown bucket. Empty cohorts produce zero counts and an explicit no-data observation. Sensitive employment/income/gender/age distributions are withheld in their entirety if any nonzero cell has fewer than five records: every count/percentage is null, preventing subtraction of visible cells from the total. Insights only use unsuppressed distributions and never reveal withheld values. Fixed labels/aggregates only are serialized; names, telephone numbers, identifiers, DOBs, addresses, tokens and integration references are excluded. Map drilldown provides protected request links, not demographic data.

Insights are computed from the selected request-type distribution, largest publishable gender and age groups, and reported no-income share. They contain no hardcoded example statistics. The accessible data table gives the same values/definitions as charts and tooltips.

## Verification

Focused PostgreSQL tests cover cohort/date boundaries, parish aliases and unknowns, real categories, birthday age boundaries, monthly income/employment bins, legacy defaults, verified integration outcomes, empty cohorts, insights, suppression and drilldown consistency. Real Razor/policy tests cover Reports ordering, shell/layout, page/JSON/drilldown authorization, custom filters and no-store responses. Browser checks use sanitized fixtures for desktop and mobile, with the real local libraries and pinned geography. No artificial production intakes are required.
