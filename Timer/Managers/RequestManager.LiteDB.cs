/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LiteDB;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;
using SurfTimer.Managers.Request.Models;

namespace SurfTimer.Managers;

internal class RequestManagerLiteDB : IManager, IRequestManager
{
    private readonly LiteDatabase                  _database;
    private readonly ILogger<RequestManagerLiteDB> _logger;

    private const string PlayerRecordTableName           = "player_records";
    private const string PlayerStageRecordTableName      = "player_stage_records";
    private const string PlayerCheckpointRecordTableName = "player_checkpoint_records";

    private const string MapTableName  = "maps";
    private const string UserTableName = "users";

    static RequestManagerLiteDB()
    {
        var mapper = BsonMapper.Global;

        mapper.Entity<MapProfile>()
              .Id(x => x.MapId);

        mapper.Entity<RunRecord>()
              .Id(x => x.Id);

        mapper.Entity<PlayerProfile>()
              .Id(x => x.Id);
    }

    public RequestManagerLiteDB(InterfaceBridge bridge, ILogger<RequestManagerLiteDB> logger)
    {
        var dbPath = Path.Combine(bridge.SharpPath, "data", "surftimer", "timer.db");
        _database = new (dbPath);
        _logger   = logger;
    }

    public bool Init()
    {
        EnsureIndexes();

        return true;
    }

    public void Shutdown()
    {
        _database.Dispose();
    }

    private void EnsureIndexes()
    {
        var recordCol = _database.GetCollection<RunRecord>(PlayerRecordTableName);
        recordCol.EnsureIndex(x => x.MapId);
        recordCol.EnsureIndex(x => x.Style);
        recordCol.EnsureIndex(x => x.Track);
        recordCol.EnsureIndex(x => x.Stage);
        recordCol.EnsureIndex(x => x.Time);

        recordCol.EnsureIndex(x => x.PlayerId);

        var checkpointCol = _database.GetCollection<RunCheckpoint>(PlayerCheckpointRecordTableName);
        checkpointCol.EnsureIndex(x => x.RecordId);
    }

    public Task<MapProfile> GetMapInfo(string map)
    {
        var col = _database.GetCollection<MapProfile>(MapTableName);
        col.EnsureIndex(i => i.MapId);

        if (col.FindOne(i => i.MapName.Equals(map, StringComparison.OrdinalIgnoreCase)) is { } rec)
        {
            return Task.FromResult(rec);
        }

        var newMap = new MapProfile
        {
            Bonuses = 0,
            Stages  = 0,

            MapName = map,

            PlayCount     = 0,
            TotalPlayTime = 0.0f,

            Tier = new byte[Utils.MAX_TRACK],
        };

        var newId = col.Insert(newMap);
        newMap.MapId = newId;

        return Task.FromResult(newMap);
    }

    public Task UpdateMapInfo(MapProfile info)
    {
        var col = _database.GetCollection<MapProfile>(MapTableName);
        col.EnsureIndex(i => i.MapId);
        col.EnsureIndex(i => i.MapName);

        if (col.FindOne(i => i.MapName.Equals(info.MapName, StringComparison.OrdinalIgnoreCase)) is { } existingInfo)
        {
            existingInfo.Tier    = info.Tier;
            existingInfo.Bonuses = info.Bonuses;
            existingInfo.Stages  = info.Stages;

            existingInfo.PlayCount     = info.PlayCount;
            existingInfo.TotalPlayTime = info.TotalPlayTime;
            col.Update(existingInfo);
        }
        else
        {
            col.Insert(info);
        }

        return Task.CompletedTask;
    }

    public Task<List<RunRecord>> GetMapRecords(Guid mapId)
    {
        var col = _database.GetCollection<RunRecord>(PlayerRecordTableName);

        col.EnsureIndex(i => i.Id);

        return Task.FromResult(col.Query()
                                  .Where(i => i.MapId == mapId)
                                  .OrderBy(i => i.Time)
                                  .ToList());
    }

    public Task<List<RunRecord>> GetMapStageRecords(Guid mapId)
    {
        var col = _database.GetCollection<RunRecord>(PlayerStageRecordTableName);

        col.EnsureIndex(i => i.Id);

        return Task.FromResult(col.Query()
                                  .Where(i => i.MapId == mapId)
                                  .OrderBy(i => i.Time)
                                  .ToList());
    }

    public Task<List<RunRecord>> GetMapRecords(Guid mapId, int style, int track)
    {
        var col = _database.GetCollection<RunRecord>(PlayerRecordTableName);

        col.EnsureIndex(i => i.Id);

        return Task.FromResult(col.Query()
                                  .Where(i => i.MapId == mapId && i.Style == style && i.Track == track && i.Stage == 0)
                                  .OrderBy(i => i.Time)
                                  .ToList());
    }

    public Task<List<RunRecord>> GetMapStageRecords(Guid mapId, int style, int track, int stage)
    {
        var col = _database.GetCollection<RunRecord>(PlayerStageRecordTableName);

        col.EnsureIndex(x => new
        {
            x.MapId,
            x.Stage,
            x.Style,
            x.Track,
            x.Time,
        });

        return Task.FromResult(col.Query()
                                  .Where(i => i.MapId    == mapId
                                              && i.Stage == stage
                                              && i.Style == style
                                              && i.Track == track)
                                  .OrderBy(i => i.Time)
                                  .ToList());
    }

    public Task<(EAttemptResult, RunRecord)> AddPlayerRecord(Guid          playerId,
                                                             Guid          mapId,
                                                             RecordRequest recordRequest)
    {
        var recordCol     = _database.GetCollection<RunRecord>(PlayerRecordTableName);
        var checkpointCol = _database.GetCollection<RunCheckpoint>(PlayerCheckpointRecordTableName);

        var newRecord = new RunRecord
        {
            MapId    = mapId,
            PlayerId = playerId,

            Time = recordRequest.Time,

            Style = recordRequest.Style,
            Track = recordRequest.Track,

            Jumps   = recordRequest.Jumps,
            Strafes = recordRequest.Strafes,
            Sync    = recordRequest.Sync,

            Stage = 0,

            RunDate = DateTime.Now,
        };

        newRecord.SetStartVelocity(recordRequest.GetStartVelocity());
        newRecord.SetAverageVelocity(recordRequest.GetAverageVelocity());
        newRecord.SetEndVelocity(recordRequest.GetEndVelocity());

        _database.BeginTrans();

        try
        {
            var newRecordId = recordCol.Insert(newRecord);
            newRecord.Id = newRecordId;

            if (recordRequest.Checkpoints?.Count > 0)
            {
                var checkpoints = recordRequest.Checkpoints.Select((cp, i) =>
                {
                    var cpInfo = new RunCheckpoint
                    {
                        RecordId        = newRecordId,
                        CheckpointIndex = (uint) (i + 1),
                        Time            = cp.Time,
                        Sync            = cp.Sync,
                    };

                    cpInfo.SetTouchVelocity(cp.GetTouchVelocity());
                    cpInfo.SetAverageVelocity(cp.GetAverageVelocity());

                    return cpInfo;
                });

                checkpointCol.InsertBulk(checkpoints);
            }

            var existingBests = recordCol.Query()
                                         .Where(r => r.MapId    == newRecord.MapId
                                                     && r.Stage == newRecord.Stage
                                                     && r.Style == newRecord.Style
                                                     && r.Track == newRecord.Track
                                                     && r.Id    != newRecord.Id)
                                         .OrderBy(r => r.Time)
                                         .Select(r => new
                                         {
                                             r.Id,
                                             r.PlayerId,
                                         })
                                         .ToList();

            var serverBest = existingBests.FirstOrDefault();
            var playerBest = existingBests.FirstOrDefault(r => r.PlayerId == playerId);

            EAttemptResult result;

            if (serverBest == null || newRecord.Time < recordCol.FindById(serverBest.Id).Time)
            {
                result = EAttemptResult.NewServerRecord;
            }
            else if (playerBest == null || newRecord.Time < recordCol.FindById(playerBest.Id).Time)
            {
                result = EAttemptResult.NewPersonalRecord;
            }
            else
            {
                result = EAttemptResult.NoNewRecord;
            }

            _database.Commit();

            return Task.FromResult((result, newRecord));
        }
        catch (Exception)
        {
            _database.Rollback();

            throw;
        }
    }

    public Task<List<RunRecord>> GetPlayerRecords(Guid playerId, Guid mapId)
    {
        var col = _database.GetCollection<RunRecord>(PlayerRecordTableName);

        col.EnsureIndex(x => x.Id);

        var allPlayerRuns = col.Query()
                               .Where(r => r.PlayerId == playerId && r.MapId == mapId && r.Stage == 0)
                               .ToEnumerable();

        List<RunRecord> records = allPlayerRuns
                                  .GroupBy(run => new
                                  {
                                      run.Style,
                                      run.Track,
                                  })
                                  .Select(group => group.MinBy(run => run.Time))
                                  .Where(record => record != null)
                                  .ToList();

        return Task.FromResult(records);
    }

    public Task<RunRecord?> GetPlayerRecord(Guid playerId, Guid mapId, int style, int track)
    {
        var col = _database.GetCollection<RunRecord>(PlayerRecordTableName);

        col.EnsureIndex(x => new
        {
            x.Id,
            x.PlayerId,
            x.MapId,
            x.Stage,
            x.Style,
            x.Track,
        });

        return Task.FromResult(col.Query()
                                  .Where(r => r.PlayerId == playerId
                                              && r.MapId == mapId
                                              && r.Stage == 0
                                              && r.Style == style
                                              && r.Track == track)
                                  .OrderBy(r => r.Time)
                                  .FirstOrDefault()
                               ?? null);
    }

    public Task<(EAttemptResult, RunRecord)> AddPlayerStageRecord(Guid playerId, Guid mapId, RecordRequest recordRequest)
    {
        var recordCol     = _database.GetCollection<RunRecord>(PlayerStageRecordTableName);
        var checkpointCol = _database.GetCollection<RunCheckpoint>(PlayerCheckpointRecordTableName);

        var newRecord = new RunRecord
        {
            MapId    = mapId,
            PlayerId = playerId,

            Time = recordRequest.Time,

            Style = recordRequest.Style,
            Track = recordRequest.Track,

            Jumps   = recordRequest.Jumps,
            Strafes = recordRequest.Strafes,
            Sync    = recordRequest.Sync,

            Stage = recordRequest.Stage,

            RunDate = DateTime.Now,
        };

        newRecord.SetStartVelocity(recordRequest.GetStartVelocity());
        newRecord.SetAverageVelocity(recordRequest.GetAverageVelocity());
        newRecord.SetEndVelocity(recordRequest.GetEndVelocity());

        _database.BeginTrans();

        try
        {
            var newRecordId = recordCol.Insert(newRecord);
            newRecord.Id = newRecordId;

            if (recordRequest.Checkpoints?.Count > 0)
            {
                var checkpoints = recordRequest.Checkpoints.Select((cp, i) =>
                {
                    var cpInfo = new RunCheckpoint
                    {
                        RecordId        = newRecordId,
                        CheckpointIndex = (uint) (i + 1),
                        Time            = cp.Time,
                        Sync            = cp.Sync,
                    };

                    cpInfo.SetTouchVelocity(cp.GetTouchVelocity());
                    cpInfo.SetAverageVelocity(cp.GetAverageVelocity());

                    return cpInfo;
                });

                checkpointCol.InsertBulk(checkpoints);
            }

            var existingBests = recordCol.Query()
                                         .Where(r => r.MapId    == newRecord.MapId
                                                     && r.Stage == newRecord.Stage
                                                     && r.Style == newRecord.Style
                                                     && r.Track == newRecord.Track
                                                     && r.Id    != newRecord.Id)
                                         .OrderBy(r => r.Time)
                                         .Select(r => new
                                         {
                                             r.Id,
                                             r.PlayerId,
                                         })
                                         .ToList();

            var serverBest = existingBests.FirstOrDefault();
            var playerBest = existingBests.FirstOrDefault(r => r.PlayerId == playerId);

            EAttemptResult result;

            if (serverBest == null || newRecord.Time < recordCol.FindById(serverBest.Id).Time)
            {
                result = EAttemptResult.NewServerRecord;
            }
            else if (playerBest == null || newRecord.Time < recordCol.FindById(playerBest.Id).Time)
            {
                result = EAttemptResult.NewPersonalRecord;
            }
            else
            {
                result = EAttemptResult.NoNewRecord;
            }

            _database.Commit();

            return Task.FromResult((result, newRecord));
        }
        catch (Exception)
        {
            _database.Rollback();

            throw;
        }
    }

    public Task<List<RunRecord>> GetPlayerStageRecords(Guid playerId, Guid mapId)
    {
        var col = _database.GetCollection<RunRecord>(PlayerStageRecordTableName);

        col.EnsureIndex(x => new
        {
            x.PlayerId,
            x.MapId,
        });

        var allPlayerRunsOnMap = col.Query()
                                    .Where(i => i.PlayerId == playerId && i.MapId == mapId)
                                    .ToEnumerable();

        var records = allPlayerRunsOnMap
                      .GroupBy(run => run.Stage)
                      .Select(stageGroup => stageGroup.OrderBy(run => run.Time)
                                                      .First())
                      .OrderBy(bestRun => bestRun.Stage)
                      .ToList();

        return Task.FromResult(records);
    }

    public Task<PlayerProfile> GetPlayerProfile(SteamID steamId, string name)
    {
        var col = _database.GetCollection<PlayerProfile>(UserTableName);

        col.EnsureIndex(i => i.SteamId);

        var user = col.Query()
                      .Where(i => i.SteamId == steamId)
                      .FirstOrDefault();

        if (user != null)
        {
            user.UpdateName(name);
            col.Update(user);

            return Task.FromResult(user);
        }

        user = new ()
        {
            SteamId = steamId,
            Points  = 0,
        };

        user.UpdateName(name);

        var newId = col.Insert(user);
        user.Id = newId.AsGuid;

        return Task.FromResult(user);
    }

    public Task RemoveMapRecords(Guid mapId)
    {
        _database.GetCollection<RunRecord>(PlayerRecordTableName)
                 .DeleteMany(i => i.MapId == mapId);

        _database.GetCollection<RunRecord>(PlayerStageRecordTableName)
                 .DeleteMany(i => i.MapId == mapId);

        return Task.CompletedTask;
    }
}
