namespace Timer.Backend.Storage;

/// <summary>
/// A player on the points leaderboard. Players with the same points share a rank.
/// </summary>
public sealed record TimerBackendRankedPlayer(ulong SteamId, string Name, uint Points, int Rank);
