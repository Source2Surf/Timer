using System.Collections.Generic;

namespace Timer.Backend.Storage;

/// <summary>
/// Every run of a player removed by an admin, on every map; a dry run only counts them.
/// </summary>
public sealed record TimerBackendWipedPlayer(int Maps, int Runs, IReadOnlyList<TimerBackendWipedRun> Deleted);

/// <summary>
/// One of the runs a wipe removed, with its map and time, so a game server can drop its replay files.
/// </summary>
public sealed record TimerBackendWipedRun(string MapName, float Time, TimerBackendDeletedRun Run);
