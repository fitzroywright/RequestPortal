# Registered Users Dashboard

Route: `/Management/RegisteredUsers`. Available only with the existing management-dashboard staff authorization policy. No new database schema or writable business workflow.

## Reports menu

Exact order:

1. Quick Intakes — `/Management/QuickIntakeDashboard`
2. Registered Users — `/Management/RegisteredUsers`
3. Management — `/Management`
4. Requests — `/Staff/Reports`

Both Reports and Admin dropdowns close on an outside click, when another dropdown opens, or with Escape. The existing Management Dashboard and Requests reports implementations are untouched.

## Cohort and data contract

The selected date range filters **account registration dates** (`ApplicationUser.CreatedUtc`, UTC). Seven-, 30-, and 90-day presets and custom ranges up to 366 days are supported. Every account appears at most once. Internal staff identities are excluded.

Accounts created as Quick Intake-only placeholders (`quick-...@requestportal.local`) are excluded. Unconverted Quick Intake tracking identities are excluded by association. A genuine registered user who submits a Quick Intake remains a registered user, but the Quick Intake itself **never contributes to regular submission counts**.

* Registered Users: distinct eligible accounts created in period.
* Individual Accounts: eligible accounts with no approved membership in a non-`IndividualContext` organization.
* Organization Representatives: eligible accounts with at least one approved non-individual organization membership. Multiple memberships do not multiply a user.
* Recently Active: eligible accounts whose recorded `LastSignInUtc` is within the prior 30 days, relative to now. Historical missing sign-in timestamps cannot be reconstructed.
* Pending Memberships: pending non-individual organization membership records belonging to eligible accounts. This is a *membership count*, not a distinct account count.
* Organizations: non-individual organizations with at least one approved member in the eligible account cohort. This is an *organization count*, not a user count.
* Regular Submissions: submitted assistance requests dated in the selected period, created by eligible accounts, with **no associated QuickIntakeRecord**.
* Geography: parish from the registered account profile, normalized using the existing canonical parish names. Unknown/invalid parish is reported separately.
* Registration Trend: aggregate count of registration dates in up to eight buckets, not accumulated totals.
* Account Status: mutually exclusive temporarily locked, email confirmed, and email not confirmed states. The latter two are not organization-approval states.

The dashboard presents only counts and group labels, never individual profiles, contact details or raw dates of birth. Its page is in the existing `/Management` staff authorization folder and uses no-store responses.

## Visual

Uses approved content-area dashboard arrangement: summary row, light-green Jamaica parish map and user-type doughnut, registration trend, account-status doughnut and activity statistics. The existing Leaflet map geography and Chart.js assets are reused. The approved Quick Intake map's dark blues are changed to lighter blues without changing map geometry or aggregation.

## Verification still required

This feature branch requires successful .NET restore/build, database-backed regression tests, browser layout checks, CI resolution and deployment through the established Kratos release process. Changes must not be merged or deployed on the basis of this document alone.
