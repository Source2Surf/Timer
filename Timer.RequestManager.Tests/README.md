Run the storage regression tests with:

```sh
dotnet test Timer.RequestManager.Tests/Timer.RequestManager.Tests.csproj
```

The default tests use isolated temporary SQLite files. The fixture adapts identity
column types for SQLite without changing production mappings. SQL generation is
also checked for MySQL and PostgreSQL.

Best-run seeding coverage includes player/style/track/stage isolation, fastest-time
selection and RunId tie-breaking, partial-table repair, concurrent readers waiting
for a seed, rollback, SteamID/BIGINT round trips, and retry after a failed write.

To run the real database suites, supply `TIMER_TEST_MYSQL` and/or
`TIMER_TEST_POSTGRES` as connection strings to disposable databases, then run:

```sh
dotnet test Timer.RequestManager.Tests --filter FullyQualifiedName~DatabaseConcurrencyTests --logger "console;verbosity=detailed"
```

These suites create uniquely named test maps, players and migration tables. They
do not read application credentials. Each unset variable skips its engine suite.
They verify independent connections competing for seed/finish/map/player locks,
cross-map totals, wipe rollback, checkpoints/replay cleanup, stale score configuration,
concurrent profile creation, primitive SteamId projections and composite-key updates,
and legacy replay-column migration (including PostgreSQL rollback on invalid data).

Each engine suite also reports command counts and timings for 2,000-player cold,
changed and unchanged recalculations, plus 20 warm slower finishes. Command-count
assertions catch per-player round trips; elapsed timings are observations, not CI
thresholds or production latency guarantees. Each engine suite is one xUnit test
containing the scenarios above.
