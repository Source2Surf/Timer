using System;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    private async Task UpsertPlayerBestRunAsync(RunEntity run, ulong? rowId)
    {
        // The caller holds this map's transaction lock and already looked the row up.
        var best = new PlayerBestRunEntity
        {
            Id = rowId ?? 0,
            SteamId = run.SteamId, MapId = run.MapId, RunType = run.RunType,
            Style = run.Style, Track = run.Track, Stage = run.Stage,
            RunId = run.Id, BestTime = run.Time, BestTicks = run.Ticks, UpdatedAtUnixMilliseconds = ToUnixTimeMilliseconds(DateTime.UtcNow),
        };
        if (rowId is null)
            await _db.Insertable(best).ExecuteCommandAsync(OperationCancellation);
        else
            await _db.Updateable(best).UpdateColumns(x => new { x.RunId, x.BestTime, x.BestTicks, x.UpdatedAtUnixMilliseconds }).ExecuteCommandAsync(OperationCancellation);
    }
}
