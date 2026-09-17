Run the regression tests with `dotnet test Timer.Tests/Timer.Tests.csproj -p:CIBuild=true`.
`CIBuild=true` disables the game's local post-build copy. The tests run without a CS2 server;
the remote-sender coverage uses an in-memory queue and fake transport, with no LiteDB database.

Coverage includes personal-best cache updates, duplicate input and tied times,
out-of-order map refreshes, map changes, in-memory submission retry/quarantine behavior, and
distinct main/stage checkpoint IDs across restarts. Unacknowledged remote submissions are
explicitly not recovered after a plugin restart. The legacy-queue safety test injects an
existence result and does not create a database file.

Keep the restored ModSharp version consistent with the game project's Microsoft.Extensions
dependencies. The projects currently pin ModSharp to version 2.1.136.
For checked-arithmetic validation, also pass -p:CheckForOverflowUnderflow=true
and -p:TreatWarningsAsErrors=true.

Replay storage coverage includes captured V1 fixtures and the independently specified 35-byte
V2 fixed-point layout, low-32-bit button preservation and high-bit truncation in each
field, every spatial component's
bounds and rounding, full-width fallback, compression modes, and malformed payloads.
Allocation bounds, asynchronous block boundaries, large headers, buffer ownership,
and corrupt-file backup also have regression coverage.
See [the replay format](../docs/replay-format.md) for compatibility and byte-layout details.
