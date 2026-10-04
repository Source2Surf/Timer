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
    public Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
        => QueryBestRecordsByMapAsync(mapName,
                                      limit,
                                      RunType.Main,
                                      style: null,
                                      track: null,
                                      stage: null,
                                      orderByStageThenTime: false);

    public Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
        => QueryBestRecordsByMapAsync(mapName,
                                      limit,
                                      RunType.Stage,
                                      style: null,
                                      track: null,
                                      stage: null,
                                      orderByStageThenTime: true);

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
                                                                             bool      ensureBestRunsSeeded = true)
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

        var query = QueryBoard().Where((best, run, player) => best.MapId == mapId.Value
                                                          && best.RunType == runType);

        if (style.HasValue)
        {
            query = query.Where((best, run, player) => best.Style == style.Value);
        }

        if (track.HasValue)
        {
            query = query.Where((best, run, player) => best.Track == track.Value);
        }

        if (stage.HasValue)
        {
            query = query.Where((best, run, player) => best.Stage == stage.Value);
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

        return await ReadBoardAsync(query, normalizedLimit);
    }

    // Best runs with their run and player, so a board's names come in the same query.
    private ISugarQueryable<PlayerBestRunEntity, RunEntity, PlayerEntity> QueryBoard()
        => QueryBestRuns().InnerJoin<RunEntity>((best, run) => best.RunId == run.Id)
                          .LeftJoin<PlayerEntity>((best, run, player) => player.SteamId == run.SteamId);

    private async Task<IReadOnlyList<RunRecord>> ReadBoardAsync(ISugarQueryable<PlayerBestRunEntity, RunEntity, PlayerEntity> query,
                                                                int limit)
    {
        var rows = await query.Select((best, run, player) => new BoardRow
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
                              })
                              .Take(limit)
                              .ToListAsync(OperationCancellation);

        var result = new RunRecord[rows.Count];

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = ToRunRecord(rows[i]);
        }

        return result;
    }

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
