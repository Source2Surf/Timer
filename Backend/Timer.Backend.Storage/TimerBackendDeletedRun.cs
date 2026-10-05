using System.Collections.Generic;

namespace Timer.Backend.Storage;

/// <summary>
/// A run removed by an admin, so the game server can drop its replay files and reload what it showed.
/// </summary>
/// <param name="WasBest">It was the player's best on its board: their next-fastest run took its place, or they left the board.</param>
public sealed record TimerBackendDeletedRun(ulong                 RunId,
                                            ulong                 SteamId,
                                            bool                  StageRun,
                                            int                   Style,
                                            int                   Track,
                                            int                   Stage,
                                            bool                  WasBest,
                                            IReadOnlyList<string> ReplayUrls);
