using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Shared.Models.Replay;

public readonly record struct ReplaySaveContext
{
    public ulong          SteamId       { get; init; }
    public float          FinishTime    { get; init; }
    public EAttemptResult AttemptResult { get; init; }
}
