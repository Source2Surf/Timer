Run the regression tests with `dotnet test Timer.Tests/Timer.Tests.csproj -p:CIBuild=true`.
`CIBuild=true` disables the game's local post-build copy. The tests run without a CS2 server
and create isolated temporary LiteDB databases.

Coverage includes personal-best cache updates, duplicate input and tied times,
out-of-order map refreshes, map changes, LiteDB attempt classification, and distinct
main/stage checkpoint IDs across restarts.

Keep the restored ModSharp version consistent with the game project's Microsoft.Extensions
dependencies. The repository currently uses a wildcard for ModSharp; this audit used
the previous local build's version 2.1.136, supplied through a temporary MSBuild override.
