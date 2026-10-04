Run the storage regression tests with:

```sh
dotnet test Backend/Timer.Backend.Storage.Tests/Timer.Backend.Storage.Tests.csproj
```

The default tests use isolated temporary SQLite files. The fixture adapts identity
column types for SQLite without changing production mappings. SQL generation is
also checked for MySQL and PostgreSQL.

Best-run seeding coverage includes player/style/track/stage isolation, fastest-time
selection and RunId tie-breaking, partial-table repair, concurrent readers waiting
for a seed, rollback, SteamID/BIGINT round trips, and retry after a failed write.
SQL Outbox coverage includes transactional PB/WR enqueue, fixed-window generation
coalescing, lease expiry/reclaim, persisted exponential retry, dead-letter/reactivation,
and rollback when the Outbox mutation fails.
Submission coverage includes canonical-hash stability, exact retry, payload conflict,
full transaction rollback, missing map/player rejection, PB/WR/NoNew classification,
historical best-run cold seeding, populated master-map zero-default migration,
and populated legacy-Inbox audit-column migration.

To run the real database suites, supply `TIMER_TEST_MYSQL` and/or
`TIMER_TEST_POSTGRES` as connection strings to disposable databases, then run:

```sh
dotnet test Backend/Timer.Backend.Storage.Tests --filter FullyQualifiedName~DatabaseConcurrencyTests --logger "console;verbosity=detailed"
```

These suites create uniquely named test maps, players and migration tables, and
clear `surf_score_recalc_outbox` at startup so stale leased work cannot affect
global worker assertions. Use only disposable databases. They do not read
application credentials. Each unset variable skips its engine suite.
They verify independent connections competing for seed/finish/map/player locks,
cross-map totals, wipe rollback, checkpoints/replay cleanup, stale score configuration,
concurrent profile creation, primitive SteamId projections and composite-key updates,
legacy replay-column migration (including PostgreSQL rollback on invalid data),
populated submission-Inbox migration, concurrent Outbox generation
merge/claim/late-completion handoff, 100 same-ID submissions, and a cross-map
same-ID payload conflict. The submission concurrency assertions require exactly
one run, one checkpoint set, one Inbox result, and one Outbox generation.

Each engine suite also reports command counts and timings for 2,000-player cold,
changed and unchanged recalculations, plus 20 warm slower finishes. Command-count
assertions catch per-player round trips; elapsed timings are observations, not CI
thresholds or production latency guarantees. Each engine suite is one xUnit test
containing the scenarios above.

Backend resilience acceptance is available through
`pwsh -File scripts/test-backend-acceptance.ps1 -DisposableDatabases`.
It requires both loopback test databases, runs the real master migration first,
then executes storage/backend/plugin suites including TLS, and rejects skipped
tests. See `Backend/Timer.Backend/README.md` for the destructive test-database setup.

`DatabaseCancellationTests` checks both production providers with independent
connections: cancelling a locked submission does not block a concurrent read or
poison the next request, and an interrupted worker releases its lease without
consuming retries before a replacement worker finishes the pending calculation.
SQLite regression tests also cancel after Inbox finalization to verify the
entire run/checkpoint/best/Inbox/Outbox transaction rolls back.
`DatabaseIndexReadinessTests` replaces required indexes with same-named ordinary,
wrong-column, expanded-key and wrong-table indexes in both disposable databases.
Readiness and migration must reject malformed uniqueness guarantees, and accept the
restored schema. `DatabaseSubmissionValidationTests` verifies invalid completion
timestamps are rejected without writes, while the shared valid boundaries still
persist checkpoints and replay idempotently. The backend HTTP/2 and TLS round trips
also verify these timestamp errors reach clients as `InvalidArgument`.

`DatabaseReviewRegressionTests` uses independent MySQL/PostgreSQL contexts to
verify complete zone snapshot replacement, slow recalculations surviving up to
eight successive lease claims, and populated Outbox age-column migration.
Competing claimants skip completed work, and an in-flight success clears a
dead letter caused by lease expiry. SQLite tests verify score/total rollback
when completion fails, and retained receipts preventing a wiped run's replay.
Backend resilience tests cover cancellation and disposal of administrative work,
plus persistent lag alerts when new generations repeatedly reset delivery retries.

`OutboxBatchClaimRegressionTests` verifies in both real database providers that
a repeatedly blocked batch head cannot dead-letter healthy, unstarted tail work
when all old claimants shut down. A controllable clock checks that later claims
receive their full lease from their actual start time. `DatabaseReceiptConsistencyTests`
compares the timestamp of an accepted submission with exact retry and status
receipts using each database's persisted precision. Write readiness regressions
reject missing map-track score configuration tables and columns.

`ReplayUploadConsistencyTests` checks replay URLs for both main and stage runs: every
personal best keeps its replay, a retry repoints the run, and a removed run takes no
URL. The plugin's `BackendReplayProviderTests` cover the upload side.
`DatabaseEdgeCaseRegressionTests` exercises SQLite and both disposable production
providers: finite session deltas, atomic time/count limits, stable first-join dates
across profile/points updates, additive and repeatable join-date migration,
historical invalid-playtime repair, and equal-time RunId ordering (including fractional and adjacent float values) for main/stage
ranks. Real-provider cases use independent contexts for concurrent profile creation,
first statistics inserts and the last available counter increment. Migration CLI
acceptance verifies the join-date backfill, and readiness rejects its missing column.