using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    // Map ids per IN list when fetching the server's best times.
    private const int SummaryMapChunk = 500;

    public async Task<PlayerSummary?> GetPlayerSummary(SteamID steamId)
    {
        var steamIdValue = ToDbSteamId(steamId);

        var player = await _db.Queryable<PlayerEntity>()
                              .Where(x => x.SteamId == steamIdValue)
                              .FirstAsync(OperationCancellation);

        var mine = await _db.Queryable<PlayerBestRunEntity>()
                            .Where(x => x.SteamId == steamIdValue)
                            .Select(x => new SummaryBestRow
                            {
                                MapId     = x.MapId,
                                RunType   = x.RunType,
                                Style     = x.Style,
                                Track     = x.Track,
                                Stage     = x.Stage,
                                BestTime  = x.BestTime,
                                BestTicks = x.BestTicks,
                            })
                            .ToListAsync(OperationCancellation);

        // The server's best on each leaderboard the player is on, to tell which of theirs are records. A tie
        // counts as held.
        var serverBest = new Dictionary<(ulong, RunType, int, ushort, ushort), SummaryBestRow>();

        foreach (var maps in mine.Select(x => x.MapId).Distinct().Chunk(SummaryMapChunk))
        {
            var rows = await _db.Queryable<PlayerBestRunEntity>()
                                .Where(x => maps.Contains(x.MapId))
                                .GroupBy(x => new { x.MapId, x.RunType, x.Style, x.Track, x.Stage })
                                .Select(x => new SummaryBestRow
                                {
                                    MapId     = x.MapId,
                                    RunType   = x.RunType,
                                    Style     = x.Style,
                                    Track     = x.Track,
                                    Stage     = x.Stage,
                                    BestTime  = SqlFunc.AggregateMin(x.BestTime),
                                    BestTicks = SqlFunc.AggregateMin(x.BestTicks),
                                })
                                .ToListAsync(OperationCancellation);

            foreach (var row in rows)
            {
                serverBest[(row.MapId, row.RunType, row.Style, row.Track, row.Stage)] = row;
            }
        }

        var styles = new SortedDictionary<int, StyleCounts>();

        foreach (var row in mine)
        {
            if (!styles.TryGetValue(row.Style, out var counts))
            {
                counts             = new StyleCounts();
                styles[row.Style] = counts;
            }

            var record = serverBest.TryGetValue((row.MapId, row.RunType, row.Style, row.Track, row.Stage), out var best)
                         && TimeOf(row.BestTicks, row.BestTime) <= TimeOf(best.BestTicks, best.BestTime);

            if (row.RunType == RunType.Stage)
            {
                counts.StageRecords += record ? 1 : 0;
            }
            else if (row.Track == 0)
            {
                counts.Maps++;
                counts.MapRecords += record ? 1 : 0;
            }
            else
            {
                counts.Bonuses++;
                counts.BonusRecords += record ? 1 : 0;
            }
        }

        // Few enough rows (one per map, one per map played) to add up here, which also avoids SUM over no rows.
        var bonusesPerMap = await _db.Queryable<MapEntity>().Select(x => x.Bonuses).ToListAsync(OperationCancellation);
        var playTimes = await _db.Queryable<PlayerMapStatsEntity>()
                                 .Where(x => x.SteamId == steamIdValue)
                                 .Select(x => x.PlayTime)
                                 .ToListAsync(OperationCancellation);

        return new PlayerSummary
        {
            Name         = player?.Name,
            Points       = player?.Points ?? 0,
            JoinedAt     = player?.JoinedAtUtc is { } joined
                               ? new DateTimeOffset(DateTime.SpecifyKind(joined, DateTimeKind.Utc)).ToUnixTimeMilliseconds()
                               : 0,
            TotalMaps    = bonusesPerMap.Count,
            TotalBonuses = bonusesPerMap.Sum(),
            PlayTime     = playTimes.Sum(),
            Styles = styles.Select(x => new PlayerStyleSummary(x.Key,
                                                               x.Value.Maps,
                                                               x.Value.Bonuses,
                                                               x.Value.MapRecords,
                                                               x.Value.BonusRecords,
                                                               x.Value.StageRecords))
                           .ToList(),
        };
    }

    public async Task<IReadOnlyDictionary<ulong, float>> GetCompletedMapsAsync(SteamID steamId, int style, int track)
    {
        var steamIdValue = ToDbSteamId(steamId);
        var trackValue   = (ushort) track;

        var rows = await _db.Queryable<PlayerBestRunEntity>()
                            .Where(x => x.SteamId    == steamIdValue
                                        && x.RunType == RunType.Main
                                        && x.Style   == style
                                        && x.Track   == trackValue
                                        && x.Stage   == 0)
                            .Select(x => new CompletedMapRow { MapId = x.MapId, BestTime = x.BestTime, BestTicks = x.BestTicks })
                            .ToListAsync(OperationCancellation);

        return rows.ToDictionary(x => x.MapId, x => TimeOf(x.BestTicks, x.BestTime));
    }

    private sealed class CompletedMapRow
    {
        public ulong MapId     { get; set; }
        public float BestTime  { get; set; }
        public int   BestTicks { get; set; }
    }

    private sealed class SummaryBestRow
    {
        public ulong   MapId     { get; set; }
        public RunType RunType   { get; set; }
        public int     Style     { get; set; }
        public ushort  Track     { get; set; }
        public ushort  Stage     { get; set; }
        public float   BestTime  { get; set; }
        public int     BestTicks { get; set; }
    }

    private sealed class StyleCounts
    {
        public int Maps;
        public int Bonuses;
        public int MapRecords;
        public int BonusRecords;
        public int StageRecords;
    }
}
