using System;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    private async Task UpsertPlayerBestRunAsync(RunEntity run, AttemptBestTimesRow? previous)
    {
        // The caller holds this map's transaction lock. Reuse the best-time query
        // instead of making Storageable probe the unique key again on every finish.
        if (previous?.PlayerBestTime is { } bestTime
            && (bestTime < run.Time || (bestTime == run.Time && previous.PlayerBestRunId <= run.Id))) return;

        var best = new PlayerBestRunEntity
        {
            Id = previous?.PlayerBestRowId ?? 0,
            SteamId = run.SteamId, MapId = run.MapId, RunType = run.RunType,
            Style = run.Style, Track = run.Track, Stage = run.Stage,
            RunId = run.Id, BestTime = run.Time, UpdatedAt = DateTime.UtcNow,
        };
        if (best.Id == 0)
            await _db.Insertable(best).ExecuteCommandAsync(OperationCancellation);
        else
            await _db.Updateable(best).UpdateColumns(x => new { x.RunId, x.BestTime, x.UpdatedAt }).ExecuteCommandAsync(OperationCancellation);
    }
}
