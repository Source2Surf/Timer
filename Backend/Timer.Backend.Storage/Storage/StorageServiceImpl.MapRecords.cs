using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    // Every board of the map, each capped at limit: the plugin caches boards one by one.
    public Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
        => QueryBestRecordsByMapAsync(mapName,
                                      limit,
                                      RunType.Main,
                                      style: null,
                                      track: null,
                                      stage: null,
                                      orderByStageThenTime: false,
                                      limitPerBoard: true);

    public Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
        => QueryBestRecordsByMapAsync(mapName,
                                      limit,
                                      RunType.Stage,
                                      style: null,
                                      track: null,
                                      stage: null,
                                      orderByStageThenTime: true,
                                      limitPerBoard: true);

    public Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName,
                                                        int    style,
                                                        int    track,
                                                        int    limit = IRequestManager.DefaultRecordLimit)
    {
        var trackValue = ToUInt16(track);

        return QueryBestRecordsByMapAsync(mapName,
                                          limit,
                                          RunType.Main,
                                          style,
                                          trackValue,
                                          stage: 0,
                                          orderByStageThenTime: false);
    }

    public Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName,
                                                             int    style,
                                                             int    track,
                                                             int    stage,
                                                             int    limit = IRequestManager.DefaultRecordLimit)
    {
        var trackValue = ToUInt16(track);
        var stageValue = ToUInt16(stage);

        return QueryBestRecordsByMapAsync(mapName,
                                          limit,
                                          RunType.Stage,
                                          style,
                                          trackValue,
                                          stageValue,
                                          orderByStageThenTime: false);
    }

    /// <summary>
    /// Read-only API query shape. The plugin-facing IRequestManager predates optional
    /// style/track/stage filters, whereas the HTTP API needs to represent those filters
    /// independently without materializing an unbounded list in the web process.
    /// </summary>
    internal Task<IReadOnlyList<RunRecord>> GetMapRecordsForReadApiAsync(string mapName,
                                                                        bool   stageRecords,
                                                                        int?   style,
                                                                        int?   track,
                                                                        int?   stage,
                                                                        int    limit,
                                                                        bool   allowReadRepair)
    {
        return QueryBestRecordsByMapAsync(mapName,
                                          limit,
                                          stageRecords ? RunType.Stage : RunType.Main,
                                          style,
                                          track.HasValue ? ToUInt16(track.Value) : null,
                                          stage.HasValue ? ToUInt16(stage.Value) : null,
                                          orderByStageThenTime: stageRecords && !stage.HasValue,
                                          ensureBestRunsSeeded: allowReadRepair);
    }

    private async Task<IReadOnlyList<RunRecord>> QueryBestRecordsByMapAsync(string    mapName,
                                                                             int       limit,
                                                                             RunType   runType,
                                                                             int?      style,
                                                                             ushort?   track,
                                                                             ushort?   stage,
                                                                             bool      orderByStageThenTime,
                                                                             bool      ensureBestRunsSeeded = true,
                                                                             bool      limitPerBoard = false)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return [];
        }

        if (ensureBestRunsSeeded && style.HasValue && track.HasValue && stage.HasValue)
        {
            await EnsureBestRunsSeededAsync(mapId.Value, runType, style.Value, track.Value, stage.Value);
        }
        else if (ensureBestRunsSeeded)
        {
            await EnsureBestRunsSeededForMapAsync(mapId.Value, runType);
        }

        var normalizedLimit = NormalizeLimit(limit);
        var shape = $"board:{runType}:{style.HasValue}:{track.HasValue}:{stage.HasValue}:{orderByStageThenTime}:{limitPerBoard}:{normalizedLimit}";
        var read  = CachedShape(shape,
                                () => limitPerBoard
                                          ? BoardsSql(runType, style.HasValue, track.HasValue, stage.HasValue, orderByStageThenTime, normalizedLimit)
                                          : BoardSql(runType, style.HasValue, track.HasValue, stage.HasValue, orderByStageThenTime, normalizedLimit),
                                BoardArguments(Sentinel.MapId, style.HasValue ? Sentinel.Style : null,
                                               track.HasValue ? Sentinel.Track : null, stage.HasValue ? Sentinel.Stage : null));

        return await ReadRunRecordsAsync(read, BoardArguments(mapId.Value, style, track, stage));
    }

    // In the order BoardSql binds them; a filter that isn't there has no argument.
    private static object[] BoardArguments(ulong mapId, int? style, ushort? track, ushort? stage)
    {
        var arguments = new List<object>(4) { mapId };
        if (style.HasValue) arguments.Add(style.Value);
        if (track.HasValue) arguments.Add(track.Value);
        if (stage.HasValue) arguments.Add(stage.Value);
        return arguments.ToArray();
    }

    private KeyValuePair<string, List<SugarParameter>> BoardSql(RunType runType, bool byStyle, bool byTrack, bool byStage,
                                                                 bool orderByStageThenTime, int limit)
    {
        var mapId = Sentinel.MapId;
        var style = Sentinel.Style;
        var track = Sentinel.Track;
        var stage = Sentinel.Stage;
        var query = QueryBoard().Where((best, run, player) => best.MapId == mapId && best.RunType == runType);

        if (byStyle)
        {
            query = query.Where((best, run, player) => best.Style == style);
        }

        if (byTrack)
        {
            query = query.Where((best, run, player) => best.Track == track);
        }

        if (byStage)
        {
            query = query.Where((best, run, player) => best.Stage == stage);
        }
        else if (runType == RunType.Main)
        {
            query = query.Where((best, run, player) => best.Stage == 0);
        }
        else
        {
            query = query.Where((best, run, player) => best.Stage > 0);
        }

        if (orderByStageThenTime)
        {
            query = query.OrderBy((best, run, player) => best.Stage)
                         .OrderBy((best, run, player) => best.BestTime)
                         .OrderBy((best, run, player) => best.RunId);
        }
        else
        {
            query = query.OrderBy((best, run, player) => best.BestTime)
                         .OrderBy((best, run, player) => best.RunId);
        }

        return SelectBoard(query).Take(limit).ToSql();
    }

    // The top rows of each board: ranked within (Style, Track, Stage) on the board index, then joined.
    private KeyValuePair<string, List<SugarParameter>> BoardsSql(RunType runType, bool byStyle, bool byTrack, bool byStage,
                                                                  bool orderByStageThenTime, int limit)
    {
        var mapId = Sentinel.MapId;
        var style = Sentinel.Style;
        var track = Sentinel.Track;
        var stage = Sentinel.Stage;
        var bests = QueryBestRuns().Where(x => x.MapId == mapId && x.RunType == runType);

        if (byStyle)
        {
            bests = bests.Where(x => x.Style == style);
        }

        if (byTrack)
        {
            bests = bests.Where(x => x.Track == track);
        }

        if (byStage)
        {
            bests = bests.Where(x => x.Stage == stage);
        }
        else if (runType == RunType.Main)
        {
            bests = bests.Where(x => x.Stage == 0);
        }
        else
        {
            bests = bests.Where(x => x.Stage > 0);
        }

        var ranked = bests.Select(x => new RankedBestRow
                          {
                              RunId     = x.RunId,
                              Stage     = x.Stage,
                              BestTime  = x.BestTime,
                              BoardRank = SqlFunc.RowNumber($"{x.BestTime} ASC, {x.RunId} ASC", $"{x.Style}, {x.Track}, {x.Stage}"),
                          })
                          .MergeTable()
                          .Where(x => x.BoardRank <= limit);

        var query = _db.Queryable(ranked)
                       .InnerJoin<RunEntity>((best, run) => best.RunId == run.Id)
                       .LeftJoin<PlayerEntity>((best, run, player) => player.SteamId == run.SteamId);

        if (orderByStageThenTime)
        {
            query = query.OrderBy((best, run, player) => best.Stage);
        }

        query = query.OrderBy((best, run, player) => best.BestTime)
                     .OrderBy((best, run, player) => best.RunId);

        return SelectBoard(query).ToSql();
    }

    private sealed class RankedBestRow
    {
        public ulong  RunId     { get; set; }
        public ushort Stage     { get; set; }
        public float  BestTime  { get; set; }
        public long   BoardRank { get; set; }
    }

    // Best runs with their run and player, so a board's names come in the same query.
    private ISugarQueryable<PlayerBestRunEntity, RunEntity, PlayerEntity> QueryBoard()
        => QueryBestRuns().InnerJoin<RunEntity>((best, run) => best.RunId == run.Id)
                          .LeftJoin<PlayerEntity>((best, run, player) => player.SteamId == run.SteamId);

    private static ISugarQueryable<BoardRow> SelectBoard<TBest>(ISugarQueryable<TBest, RunEntity, PlayerEntity> query)
        => query.Select((best, run, player) => new BoardRow
        {
            Id                       = SqlFunc.ToInt64(run.Id),
            DateUnixTimeMilliseconds = run.DateUnixTimeMilliseconds,
            SteamId                  = run.SteamId,
            PlayerName               = player.Name,
            MapId                    = SqlFunc.ToInt64(run.MapId),
            Style                    = run.Style,
            Track                    = SqlFunc.ToInt32(run.Track),
            Stage                    = SqlFunc.ToInt32(run.Stage),
            Time                     = run.Time,
            Jumps                    = SqlFunc.ToInt64(run.Jumps),
            Strafes                  = SqlFunc.ToInt64(run.Strafes),
            Sync                     = run.Sync,
            VelocityStartX           = run.VelocityStartX,
            VelocityStartY           = run.VelocityStartY,
            VelocityStartZ           = run.VelocityStartZ,
            VelocityAvgX             = run.VelocityAvgX,
            VelocityAvgY             = run.VelocityAvgY,
            VelocityAvgZ             = run.VelocityAvgZ,
            VelocityEndX             = run.VelocityEndX,
            VelocityEndY             = run.VelocityEndY,
            VelocityEndZ             = run.VelocityEndZ,
        });

    public Task<IReadOnlyList<RunRecord>> GetRecentRecords(string mapName, SteamID steamId, int limit = 10)
        => GetPlayerRunsCoreAsync(mapName, steamId, null, limit, false);

    public Task<IReadOnlyList<RunRecord>> GetPlayerRuns(string mapName, SteamID steamId, int style, int track, int stage, int limit = 10)
        => GetPlayerRunsCoreAsync(mapName, steamId, (style, track, stage), limit, true);

    // A player's finishes on the map, newest first: on one leaderboard, or every full-map run when board is null.
    // With personalBestsOnly, just the runs that were their PB when they set them.
    private async Task<IReadOnlyList<RunRecord>> GetPlayerRunsCoreAsync(string mapName, SteamID steamId,
                                                                       (int Style, int Track, int Stage)? board, int limit,
                                                                       bool personalBestsOnly)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return [];
        }

        var normalizedLimit = NormalizeLimit(limit);
        var steamIdValue    = ToDbSteamId(steamId);

        var query = _db.Queryable<RunEntity>()
                       .Where(x => x.MapId == mapId.Value && x.SteamId == steamIdValue);

        if (board is { } b)
        {
            var runType = b.Stage == 0 ? RunType.Main : RunType.Stage;
            var style   = b.Style;
            var track   = (ushort) b.Track;
            var stage   = (ushort) b.Stage;

            query = query.Where(x => x.RunType == runType && x.Style == style && x.Track == track && x.Stage == stage);
        }
        else
        {
            query = query.Where(x => x.RunType == RunType.Main && x.Stage == 0);
        }

        if (personalBestsOnly)
        {
            // No earlier run of theirs on the board was as fast.
            query = query.Where(x => SqlFunc.Subqueryable<RunEntity>()
                                            .Where(p => p.MapId     == x.MapId
                                                        && p.SteamId == x.SteamId
                                                        && p.RunType == x.RunType
                                                        && p.Style   == x.Style
                                                        && p.Track   == x.Track
                                                        && p.Stage   == x.Stage
                                                        && p.Id      < x.Id
                                                        && p.Time    <= x.Time)
                                            .NotAny());
        }

        var rows = await query
                            .OrderByDescending(x => x.DateUnixTimeMilliseconds)
                            .OrderByDescending(x => x.Id)
                            .Select(x => new RecentRunRow
                            {
                                Id = x.Id,
                                DateUnixTimeMilliseconds = x.DateUnixTimeMilliseconds,
                                SteamId = x.SteamId,
                                MapId = x.MapId,
                                Style = x.Style,
                                Track = x.Track,
                                Stage = x.Stage,
                                Time = x.Time,
                            })
                            .Take(normalizedLimit)
                            .ToListAsync(OperationCancellation);

        var result = new List<RunRecord>(rows.Count);

        foreach (var row in rows)
        {
            result.Add(new RunRecord
            {
                Id = (long)row.Id,
                RunDate = FromUnixTimeMilliseconds(row.DateUnixTimeMilliseconds),
                SteamId = unchecked((ulong)row.SteamId),
                MapId = row.MapId,
                Style = row.Style,
                Track = row.Track,
                Stage = row.Stage,
                Time = row.Time,
            });
        }

        return result;
    }

    private sealed class RecentRunRow
    {
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = "Date", ColumnDataType = "bigint")]
        public long DateUnixTimeMilliseconds { get; set; }

        [SugarColumn(ColumnDataType = "bigint")]
        public long SteamId { get; set; }

        public ulong MapId { get; set; }

        public int Style { get; set; }

        public ushort Track { get; set; }

        public ushort Stage { get; set; }

        public float Time { get; set; }
    }
}
