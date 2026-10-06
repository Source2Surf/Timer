using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    /// <summary>
    /// Deletes every run of a player on every map with their checkpoints, replay rows and best runs, and requeues the
    /// scores of each main board they were on, which drops their points. One map at a time, each under its own lock.
    /// A dry run only counts the runs and maps.
    /// </summary>
    internal async Task<TimerBackendWipedPlayer> WipePlayerRunsAsync(SteamID player, bool dryRun,
                                                                     IReadOnlyDictionary<int, double> styleFactors)
    {
        var steamId = unchecked((long)player.AsPrimitive());

        var boards = await _db.Queryable<RunEntity>()
                              .Where(x => x.SteamId == steamId)
                              .Select(x => new WipeBoardRow
                              {
                                  MapId = x.MapId, RunType = x.RunType, Style = x.Style, Track = x.Track, Stage = x.Stage,
                              })
                              .Distinct()
                              .ToListAsync(OperationCancellation);
        var maps = boards.Select(x => x.MapId).Distinct().Order().ToList();

        if (dryRun || maps.Count == 0)
        {
            var runs = maps.Count == 0
                ? 0
                : await _db.Queryable<RunEntity>().Where(x => x.SteamId == steamId).CountAsync(OperationCancellation);

            return new TimerBackendWipedPlayer(maps.Count, runs, []);
        }

        var names = (await _db.Queryable<MapEntity>()
                              .Where(x => maps.Contains(x.MapId))
                              .Select(x => new WipeMapRow { MapId = x.MapId, File = x.File })
                              .ToListAsync(OperationCancellation))
            .ToDictionary(x => x.MapId, x => x.File);

        var deleted = new List<TimerBackendWipedRun>();
        var wiped   = 0;
        var wakeScoreRecalcWorker = false;

        foreach (var mapId in maps)
        {
            // Local seed gates must always be acquired before database locks.
            foreach (var board in boards.Where(x => x.MapId == mapId))
            {
                await EnsureBestRunsSeededAsync(mapId, board.RunType, board.Style, board.Track, board.Stage);
            }

            var mapDeleted = new List<TimerBackendWipedRun>();
            var queued     = false;

            await WithRecordTransactionAsync(async () =>
            {
                mapDeleted.Clear();
                queued = false;
                await LockMapAsync(mapId);

                var runs = await _db.Queryable<RunEntity>()
                                    .Where(x => x.SteamId == steamId && x.MapId == mapId)
                                    .Select(x => new WipeRunRow
                                    {
                                        Id = x.Id, RunType = x.RunType, Style = x.Style, Track = x.Track, Stage = x.Stage, Time = x.Time,
                                    })
                                    .ToListAsync(OperationCancellation);

                if (runs.Count == 0)
                {
                    return;
                }

                var best = (await QueryBestRuns().Where(x => x.SteamId == steamId && x.MapId == mapId)
                                                 .Select(x => x.RunId)
                                                 .ToListAsync(OperationCancellation)).ToHashSet();
                var replays = new Dictionary<ulong, List<string>>();

                // Chunked: the IN-list renders as inlined literals.
                var ids = runs.Select(x => x.Id).ToList();

                for (var offset = 0; offset < ids.Count; offset += 5000)
                {
                    var chunk = ids.GetRange(offset, Math.Min(5000, ids.Count - offset));

                    foreach (var replay in await _db.Queryable<ReplayEntity>()
                                                    .Where(x => chunk.Contains(x.RunId))
                                                    .Select(x => new WipeReplayRow { RunId = x.RunId, Replay = x.Replay })
                                                    .ToListAsync(OperationCancellation))
                    {
                        if (!replays.TryGetValue(replay.RunId, out var urls))
                        {
                            replays[replay.RunId] = urls = [];
                        }

                        urls.Add(replay.Replay);
                    }

                    await _db.Deleteable<ReplayEntity>().Where(x => chunk.Contains(x.RunId)).ExecuteCommandAsync(OperationCancellation);
                    await _db.Deleteable<RunSegmentEntity>().Where(x => chunk.Contains(x.RunId)).ExecuteCommandAsync(OperationCancellation);
                }

                await _db.Deleteable<RunEntity>().Where(x => x.SteamId == steamId && x.MapId == mapId).ExecuteCommandAsync(OperationCancellation);
                await _db.Deleteable<PlayerBestRunEntity>().Where(x => x.SteamId == steamId && x.MapId == mapId)
                         .ExecuteCommandAsync(OperationCancellation);

                var now = DateTime.UtcNow;

                foreach (var (style, track) in runs.Where(x => x.RunType == RunType.Main && best.Contains(x.Id))
                                                   .Select(x => (x.Style, x.Track))
                                                   .Distinct())
                {
                    // Like a finish: the style's configured factor, 1 when it has none.
                    var styleFactor = styleFactors.TryGetValue(style, out var factor) ? factor : 1;
                    await EnqueueScoreRecalcInCurrentRecordTransactionAsync(mapId, style, track, styleFactor, now);
                    queued = true;
                }

                var mapName = names.GetValueOrDefault(mapId, string.Empty);

                foreach (var run in runs)
                {
                    mapDeleted.Add(new TimerBackendWipedRun(mapName, run.Time,
                                                            new TimerBackendDeletedRun(run.Id, player.AsPrimitive(), run.RunType == RunType.Stage,
                                                                                       run.Style, run.Track, run.Stage, best.Contains(run.Id),
                                                                                       replays.GetValueOrDefault(run.Id) ?? [])));
                }
            });

            deleted.AddRange(mapDeleted);
            wiped                 += mapDeleted.Count > 0 ? 1 : 0;
            wakeScoreRecalcWorker |= queued;
        }

        if (wakeScoreRecalcWorker)
        {
            WakeScoreRecalcWorker();
        }

        return new TimerBackendWipedPlayer(wiped, deleted.Count, deleted);
    }

    private sealed class WipeBoardRow
    {
        public ulong   MapId   { get; set; }
        public RunType RunType { get; set; }
        public int     Style   { get; set; }
        public ushort  Track   { get; set; }
        public ushort  Stage   { get; set; }
    }

    private sealed class WipeMapRow
    {
        public ulong  MapId { get; set; }
        public string File  { get; set; } = string.Empty;
    }

    private sealed class WipeRunRow
    {
        public ulong   Id      { get; set; }
        public RunType RunType { get; set; }
        public int     Style   { get; set; }
        public ushort  Track   { get; set; }
        public ushort  Stage   { get; set; }
        public float   Time    { get; set; }
    }

    private sealed class WipeReplayRow
    {
        public ulong  RunId  { get; set; }
        public string Replay { get; set; } = string.Empty;
    }
}
