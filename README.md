> [!WARNING]
> This project is WIP and is not ready for production.

# Timer setup

Requires **.NET 10**, ModSharp (SDK reference: `2.1.136`), and a MySQL/MariaDB or
PostgreSQL database. Create the database and accounts first.

## Install

From the repository root:

```sh
dotnet publish Timer/Timer.csproj -c Release -p:Platform=x64 -p:CIBuild=true -o sharp/modules/Timer
dotnet publish Timer.RequestManager/Timer.RequestManager.csproj -c Release -p:Platform=x64 -p:CIBuild=true -o sharp/modules/Timer.RequestManager
dotnet publish Timer.Shared/Timer.Shared.csproj -c Release -p:Platform=x64 -p:CIBuild=true -o sharp/shared/Timer.Shared
```

Merge `sharp/` into `{CS2}/game/sharp/`, preserving existing configuration.
Deploy all three published folders with their dependencies from the same build.

## RequestManager

Merge into `sharp/configs/timer.jsonc`:

```json
{
  "database": {
    "type": "postgresql",
    "host": "127.0.0.1",
    "port": 5432,
    "database_name": "timer",
    "username": "timer_game",
    "password": "change_me",
    "initialize_schema": false
  }
}
```

For MySQL, use `"type": "mysql"` and port `3306`.

- **New database:** enable `database.initialize_schema` for the first startup on
  one game server, then set it back to `false`. Alternatively, bootstrap with
  Backend's `TimerBackend:InitializeSchema=true` once.
- **Existing database:** stop all writers and back up first. Follow the
  [master migration](Timer.Backend/README.md#upgrade-an-existing-master-sql-database)
  or [earlier Backend upgrade](Timer.Backend/README.md#update-an-earlier-backend-test-bundle)
  before starting the new build.

Default `local-sql` mode uses RequestManager directly; Backend is optional.

## Backend

Copy [appsettings.example.json](Timer.Backend/appsettings.example.json) to
`Timer.Backend/appsettings.Production.json` (preserve an existing file).
Set `TimerBackend:Database:Type` and `ConnectionString` to the same database
used by RequestManager. Use a normal driver connection string without a
`mysql://` or `pgsql://` prefix.

From the repository root:

```sh
dotnet run -c Release --no-launch-profile --project Timer.Backend/Timer.Backend.csproj -- --environment Production
```

The template uses HTTP port **5081** and HTTP/2 gRPC port **5082**.
Check `http://127.0.0.1:5081/health/ready` for HTTP 200.

### Remote score writes

Set Backend's `TimerBackend:WriteApi:Enabled=true`, then merge into the game's
**`sharp/configs/core.json`**:

```json
{
  "Timer": {
    "ScoreWrite": { "Mode": "remote-write" },
    "RunSubmissionSender": { "Endpoint": "http://127.0.0.1:5082" }
  }
}
```

Start Backend before the game server. **RequestManager and its SQL credentials
are still required.** Defaults accept ruleset 1 and style 0; configure
`WriteApi:StyleFactors` for other styles.

Keep gRPC on loopback/private networking: it has no built-in authentication.
The plugin retry queue is in memory; unacknowledged runs are lost on restart.
See [Backend documentation](Timer.Backend/README.md) for deployment and operations.

## Replay storage (optional)

Merge into `sharp/configs/timer.jsonc` alongside `database`:

```json
{
  "replay": {
    "storage_base_url": "http://127.0.0.1:5080",
    "upload_non_personal_best": false
  }
}
```

Use an HTTP store supporting PUT/GET/DELETE. A local reference server is available:

```sh
cd tools/ReplayStorageServer
dotnet run
```

It stores files in `./replays/` on port `5080`. See
[upload consistency](Timer.Backend/README.md#replay-upload-consistency) and
[replay format](docs/replay-format.md) for details.
