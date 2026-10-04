using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    // The fastest run's replay on a leaderboard, the player's own when steamId is set.
    internal async Task<string?> GetReplayUrlAsync(string mapName, RunType runType, int style, int track, int stage, ulong? steamId)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);
        if (mapId is null) return null;

        var query = _db.Queryable<ReplayEntity>()
                       .InnerJoin<RunEntity>((r, run) => r.RunId == run.Id)
                       .Where((r, run) => run.MapId == mapId.Value
                                          && run.RunType == runType
                                          && run.Style == style
                                          && run.Track == (ushort)track
                                          && run.Stage == (ushort)stage);

        if (steamId.HasValue)
        {
            var sid = unchecked((long)steamId.Value);
            query = query.Where((r, run) => r.SteamId == sid);
        }

        return await query.OrderBy((r, run) => run.Time)
                          .OrderBy((r, run) => run.Id)
                          .Select((r, run) => r.Replay)
                          .FirstAsync(OperationCancellation);
    }

    internal async Task<string?> GetRunReplayUrlAsync(ulong runId)
        => await _db.Queryable<ReplayEntity>()
                    .Where(r => r.RunId == runId)
                    .Select(r => r.Replay)
                    .FirstAsync(OperationCancellation);

    internal async Task<IReadOnlyList<ulong>> GetStoredReplayRunIdsAsync(IReadOnlyList<ulong> runIds)
    {
        var stored = new List<ulong>();

        foreach (var chunk in runIds.Chunk(1000))
        {
            stored.AddRange(await _db.Queryable<ReplayEntity>()
                                     .Where(r => chunk.Contains(r.RunId))
                                     .Select(r => r.RunId)
                                     .ToListAsync(OperationCancellation));
        }

        return stored;
    }

    /// <summary>
    /// Points the run at its uploaded replay. False when the run no longer exists, so the caller can
    /// delete the unused upload.
    /// </summary>
    internal async Task<bool> SaveReplayUrlAsync(string mapName, ulong steamId, ulong runId, string url)
    {
        var mapId = await EnsureMapIdByNameAsync(mapName);
        var now   = DateTime.UtcNow;

        return await SaveReplayMetadataAsync(new ReplayEntity
        {
            MapId     = mapId,
            SteamId   = unchecked((long)steamId),
            RunId     = runId,
            Replay    = url,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }
}
