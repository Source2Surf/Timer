> [!WARNING]
> This project is WIP and is not ready for production.

# Timer setup

Requires **.NET 10**, ModSharp (SDK reference: `2.1.136`), and a MySQL/MariaDB or
PostgreSQL database. Create the database and accounts first.

## Install

From the repository root:

```sh
dotnet publish Plugin/Timer/Timer.csproj -c Release -p:Platform=x64 -p:CIBuild=true -o sharp/modules/Timer
dotnet publish Plugin/Timer.RequestManager/Timer.RequestManager.csproj -c Release -p:Platform=x64 -p:CIBuild=true -o sharp/modules/Timer.RequestManager
dotnet publish Plugin/Timer.Shared/Timer.Shared.csproj -c Release -p:Platform=x64 -p:CIBuild=true -o sharp/shared/Timer.Shared
dotnet publish Plugin/Timer.Localization/Timer.Localization.csproj -c Release -p:Platform=x64 -p:CIBuild=true -o sharp/modules/Timer.Localization
```

Merge `sharp/` into `{CS2}/game/sharp/`, preserving existing configuration.

The CI and release zips ship the configs as `configs/*.jsonc.example`, so extracting
an update never touches your settings. On a fresh install, copy the ones you don't
have yet to their real names, then fill in `timer.jsonc`:

```sh
cd sharp/configs
for f in *.example; do [ -e "${f%.example}" ] || cp "$f" "${f%.example}"; done
```
Deploy all four published folders with their dependencies from the same build.
Timer.Localization is optional (players read English without it) and needs
ModSharp's LocalizerManager module; see [Localization](#localization).

## HUD

The HUD is a Panorama layout that every client has to have mounted. The server
only drives it. The layout needs ModSharp `2.1.159` or later, which is the first
version with the Panorama API.

1. Copy `panorama/` into a workshop addon's content folder, for example
   `content/csgo_addons/<addon>/panorama/`. Compile it with
   `resourcecompiler -f -i <file>` from `game/bin/win64`, or through the addon
   tools, then publish the addon.
2. Make clients download the addon, for example with MultiAddonManager.

The layout is `panorama/layout/custom_game/surftimer/hud.vxml_c`. A layout
spawned at runtime needs the compiled resource name, `_c` included. If you ship
it at another path, set `timer_hud_layout` to that path.

Each player gets their own `custom_hud_layout` entity, networked only to them.

Commands:

- `!hud` opens the settings menu. In it, a player can click a panel to drag it.
- `!showkeys` toggles the keys panel.
- `!profile` (or `!stats`) opens your profile card; `!profile <name>` opens the card of a
  player on the server. For a picked style it shows maps and bonuses completed and
  the records held across every map, then this map's PB, stage PBs and time
  played, for a picked track.
- `!replay` opens the replay menu. A player picks a track, stage, style and a
  time on its leaderboard, and watches it on the central replay bot. Pressing E
  while spectating that bot opens the menu too.

Settings are saved per player in `sharp/data/surftimer/hud/<steamid64>.json`.

### Central replay bot

The replay menu plays runs on a central replay bot: a `timer-replay.jsonc` entry
with `"type": 1`. A new config gets one next to the looping bot. Add it to an
existing config:

```json
{ "type": 1, "idle_name": "Replay Bot (!replay)", "spectate_when_idle": true }
```

With `spectate_when_idle`, the bot waits in spectator while it has nothing to
play. It joins a random team to play a replay, and goes back to spectator when
the replay ends or is stopped. Without it, the bot stands idle in the map.

Pressing Watch moves the player to spectate the bot. The player who started a
replay controls it while they watch: back or forward 5 seconds, pause,
0.5x / 1x / 2x, and stop. So does anyone with the `timer:replay` permission
(see below). Anyone else can watch along. A replay plays once, then the bot goes
idle on Main and the default style until someone picks another.

The server record plays straight from memory. Any other run loads from its
replay file on disk, then from the replay store by run id. A leaderboard row can
also use that player's best from the store.

### My runs

The replay menu's second tab lists a player's PB history on the picked track,
stage and style: each run that was their PB when they set it, newest first.
Every PB's replay is kept, on disk and in the replay store, so the whole history
can be watched. A run that didn't beat the player's PB is up to
`timer_replay_slower_runs`: `0` (default) it isn't saved, `1` it's kept on disk
for a few days, `2` it's also uploaded to the replay store:

- **On the server:** among the player's newest `timer_replay_keep_runs` (default
  10) for that map, style and track, in
  `replays/style_<n>/recent/<steamid64>/<map>/<track>/`, or
  `timer_replay_keep_stage_runs` (default 2) for each stage. Each is deleted
  after `timer_replay_recent_max_days` (default 3, `0` for never), checked a
  minute after each map starts.
- **In the replay store:** with `2`, they're uploaded as well.

A replay that isn't on disk is fetched by run id through
`IReplayProvider.GetRunReplayAsync(runId)`.

The list itself comes from `IRequestManager.GetPlayerRuns`. Both methods have
default implementations that find nothing, so custom providers keep working
without them.

### Replay cache

With a replay store, the best runs' replays on disk are a cache of
`timer_replay_cache_size_mb` (default 2048, `0` keeps all). A minute after each
map starts, if they take more than that, the least recently watched ones are
deleted, but only those that `IReplayProvider.GetStoredRunIdsAsync` confirms
are in the store, and none watched in the last hour. A replay downloaded by
its run id is saved back to disk. Without a store, or with a provider that
doesn't implement that method, nothing is deleted.

### Profile card

Its overall stats (completions, records and total time played) come from
`IRequestManager.GetPlayerSummary`. The default body answers null, so a provider
without it shows the card without that section.

### Saved locations

Walk + inspect (SHIFT + F by default) shows or hides the saved-locations panel.
It lists the practice actions and the key the player bound to each, which the
game fills in from their own binds. It doesn't take the cursor, so players keep
playing with it up, and it can be dragged from `!hud` like the other panels.

The actions are console commands, so players can bind keys to them:

```
bind mouse4 saveloc
bind mouse5 loc
bind <key> prevloc
bind <key> nextloc
bind <key> clearloc
```

`clearloc` (and `!clearloc`) asks first: the panel shows "Press again to clear all"
for a few seconds, and a second press clears every saved location.

The chat commands (`!sl`, `!loc`, `!pl`, `!nl`) still work.

The game only looks a key up when the panel shows, so after binding a key, hide
and show the panel to see it.

### Map chooser

With the Timer.MapChooser module loaded, the HUD shows its vote and its nominate
menu (through `IMapChooser`; without the module none of this appears):

- **Vote**: a panel on the right while a vote runs, with each option's tier and
  live count. It doesn't take the cursor, so players vote mid-run with the
  chooser's keys (F3 / F4 to move, F to vote, shown with the player's own binds)
  or by typing !1, !2 … in chat.
- **!nominate**: a cursor menu like the replay menu, 8 maps a page, filtered by
  tier or to maps the player hasn't finished, with their best time on each.
- The game's round timer shows the time left on the map, extensions included.

### Localization

All of the HUD's text can be translated per player: the timer, records and splits,
the settings menu, the drag hint, the replay menu and the profile card. The Timer asks `ILocalizationProvider` from
`Timer.Shared` for each text by key. Without a provider, everyone reads English.

The `Timer.Localization` module is the default provider. It uses ModSharp's
LocalizerManager, so each player reads their game language:

```sh
dotnet publish Plugin/Timer.Localization/Timer.Localization.csproj -c Release -p:CIBuild=true -o sharp/modules/Timer.Localization
```

It ships `locales/surftimer.json` (English and Simplified Chinese) and keeps
`sharp/locales/surftimer.json` the same as it, so an update's new texts arrive on
their own. Don't edit that file. Put your changes in `sharp/locales/surftimer.custom.json`,
in the same format, which is read after it and wins:

```json
{
  "hud.line.strafes": { "zh-cn": "平移：{0}", "de-de": "Strafes: {0}" }
}
```

A language needs only the keys you translate: the rest falls back to English.
Keep the `{0}`, `{1}` placeholders. After editing, `ms_locales_reload` reloads the
files without a restart.

Chat messages are still English.

### Permissions

Admin commands are checked by ModSharp's AdminManager. Grant these in
`sharp/configs/admins.jsonc`, or `timer:*` for all of them:

- `timer:zone`: `!zone`
- `timer:tier`: `!set_tier`

Without AdminManager, Release builds refuse these commands.

Replay control is checked through `IPermissionProvider` instead. To connect your admin system, write a ModSharp
module that implements `IPermissionProvider` from `Timer.Shared`, and register
it under its identity:

```csharp
_shared.GetSharpModuleManager()
       .RegisterSharpModuleInterface<IPermissionProvider>(this, IPermissionProvider.Identity, provider);
```

`HasPermission(steamId, permission)` runs on the game thread as often as the
HUD refreshes, so keep it to a cached lookup. The permissions are constants on
the interface:

- `timer:replay` (`IPermissionProvider.ReplayControl`): control a central
  replay that another player started.

Without a provider, nobody has any of these permissions.

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
  [master migration](Backend/Timer.Backend/README.md#upgrade-an-existing-master-sql-database)
  or [earlier Backend upgrade](Backend/Timer.Backend/README.md#update-an-earlier-backend-test-bundle)
  before starting the new build.

Default `local-sql` mode uses RequestManager directly; Backend is optional.

## Backend

Copy [appsettings.example.json](Backend/Timer.Backend/appsettings.example.json) to
`Backend/Timer.Backend/appsettings.Production.json` (preserve an existing file).
Set `TimerBackend:Database:Type` and `ConnectionString` to the same database
used by RequestManager. Use a normal driver connection string without a
`mysql://` or `pgsql://` prefix.

From the repository root:

```sh
dotnet run -c Release --no-launch-profile --project Backend/Timer.Backend/Timer.Backend.csproj -- --environment Production
```

The template uses HTTP port **5081** and HTTP/2 gRPC port **5082**.
Check `http://127.0.0.1:5081/health/ready` for HTTP 200.

### Remote score writes

Set Backend's `TimerBackend:WriteApi:Enabled=true`, then set `score_write` in the
game's **`sharp/configs/timer.jsonc`**:

```jsonc
"score_write": {
  "mode": "remote-write",
  "endpoint": "http://127.0.0.1:5082"
}
```

The `Timer` block in `sharp/configs/core.json` is no longer read; move it here.

Start Backend before the game server. **RequestManager and its SQL credentials
are still required.** Defaults accept ruleset 1 and style 0; configure
`WriteApi:StyleFactors` for other styles.

Keep gRPC on loopback/private networking: it has no built-in authentication.
The plugin retry queue is in memory; unacknowledged runs are lost on restart.
See [Backend documentation](Backend/Timer.Backend/README.md) for deployment and operations.

## Replay storage (optional)

Merge into `sharp/configs/timer.jsonc` alongside `database`:

```json
{
  "replay": {
    "storage_base_url": "http://127.0.0.1:5080"
  }
}
```

Use an HTTP store supporting PUT/GET/DELETE. A local reference server is available:

```sh
cd tools/ReplayStorageServer
dotnet run
```

It stores files in `./replays/` on port `5080`. See
[upload consistency](Backend/Timer.Backend/README.md#replay-upload-consistency) and
[replay format](docs/replay-format.md) for details.
