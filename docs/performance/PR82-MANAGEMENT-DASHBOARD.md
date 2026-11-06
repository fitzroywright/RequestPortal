# PR #82: staff landing page and dashboard performance

Staff OIDC completion defaults to `/Staff/Index` (canonical URL `/Staff`). An
explicit local return URL is retained; the destination's existing authorization
still applies. External return URLs are rejected. Authenticated staff visiting
`/` redirect to Requests; applicant and anonymous home behavior is unchanged.
The staff brand links to Requests; Management Dashboard remains under Reports.
Pending staff still cannot enter Requests. No authorization policy changed.

The default Management Dashboard aggregate is shared for 45 seconds using the
application's IMemoryCache. Refresh is single-flight per cache instance and
rechecks after acquiring its lock. Cancellation and failures release the lock;
failed/cancelled builds are not cached. Filtered dashboard queries and drilldowns
remain fresh and cannot reuse the unfiltered snapshot. A successful cached page
can be up to 45 seconds old, plus its original computation time. GeneratedUtc
continues to identify the snapshot's computation start.

Cold loads still read persisted resolved locations for every request, retaining
Common.Storage ownership/integrity checks and existing missing/corrupt fallback.
Database calls remain sequential. Independent storage reads now default to 16
concurrent operations instead of eight; ManagementDashboard:LocationReadConcurrency
is clamped to 1–32. No location is skipped, no guessed parish replaces a stored
location, and no production data or schema change is introduced.

Tests cover twelve simultaneous scoped callers, one shared refresh, cache expiry
at 46 seconds (still cached at 44), source status freshness after expiry,
filtered isolation, failure recovery and cancelled waiters. Existing page tests
preserve all metrics, charts, maps, delivery analytics and authorization rules.
The paired benchmark compares serialized snapshots field for field except their
GeneratedUtc timestamps and verifies the concurrency bound and warm read counts.

## Measurement limits

Benchmarks use isolated local PostgreSQL, 1,344 synthetic requests and simulated
2 ms metadata/read latency. They measure service computation, not browser assets
or production storage. Retained TRX output gives cold/repeat timings for uncached
eight-way reads, PR caching with eight-way reads, and optimized sixteen-way reads.
Initial paired measurement: 1,344.02 ms cold / 0.004 ms cached at eight-way;
806.74 ms cold / 0.003 ms cached at sixteen-way. Environment and scheduling affect
these values; no fixed timing threshold is asserted by tests.

An anonymous production `/Management` request returns a sign-in redirect;
its duration is not dashboard performance. Authenticated production cold/warm
navigation requires an approved staff session and remains a separate acceptance
check. No authentication bypass or synthetic production account is introduced.

## Final Release validation

19 domain tests and 571 infrastructure tests passed; none failed. Ten opt-in
live SQL tests were skipped because no SQL test target was configured. No SQL
Server database was accessed by these tests.

| Controlled service benchmark | Cold | Repeat |
| --- | ---: | ---: |
| Before PR: uncached, concurrency 8 | 1,442.20 ms | 1,253.343 ms |
| PR cache, concurrency 8 | 1,574.43 ms | 0.003 ms |
| Optimized cache, concurrency 16 | 779.30 ms | 0.003 ms |

The optimized cold sample is 46% shorter than the uncached baseline; the warm
sample returns the shared object without another storage scan. These are local
controlled timings, not production loading-time claims.
