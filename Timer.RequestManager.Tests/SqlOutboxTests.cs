using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

public sealed class SqlOutboxTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-sql-outbox-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;

    public SqlOutboxTests()
    {
        _storage = new StorageServiceImpl(DbType.Sqlite,
                                          $"Data Source={_path};Pooling=False",
                                          NullLogger<StorageServiceImpl>.Instance,
                                          enableScoreRecalcWorker: false);
        _storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        _storage.Init();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlannedCancellationReleasesClaimsWithoutConsumingRetryBudget(bool cancelAfterClaim)
    {
        var map = await _storage.GetMapInfo($"surf_outbox_cancel_{Guid.NewGuid():N}");
        var now = DateTime.UtcNow;
        await _storage.EnqueueScoreRecalcAsync(map.MapId, 0, 0, 1, now.AddSeconds(-10));
        using var cancellation = new CancellationTokenSource();
        var injected = false;
        _storage.Db.Aop.OnLogExecuted = (sql, _) =>
        {
            var matches = cancelAfterClaim
                ? IsOutboxUpdate(sql)
                : sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                  && sql.Contains("surf_maps", StringComparison.OrdinalIgnoreCase);
            if (matches && !injected)
            {
                injected = true;
                cancellation.Cancel();
            }
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _storage.RunOperationAsync(() => _storage.ProcessScoreRecalcOutboxBatchAsync(now, "stopping-worker"),
                    cancellation.Token));
        }
        finally { _storage.Db.Aop.OnLogExecuted = null; }

        Assert.True(injected);
        var pending = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>().FirstAsync(CancellationToken.None);
        Assert.Null(pending.LeaseOwner);
        Assert.Null(pending.LeaseUntilUtc);
        Assert.Null(pending.DeadLetteredAtUtc);
        Assert.Equal(0, pending.AttemptCount);
        Assert.Equal(0, pending.ProcessedGeneration);
        Assert.Equal(1, pending.RequestedGeneration);
        Assert.Equal(1, await _storage.ProcessScoreRecalcOutboxBatchAsync(now, "replacement-worker"));
        var completed = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>().FirstAsync();
        Assert.Equal(completed.RequestedGeneration, completed.ProcessedGeneration);
    }

    [Fact]
    public async Task EnqueueMergesSameKeyWithoutExtendingTheFirstDebounceWindow()
    {
        var map = await _storage.GetMapInfo($"surf_outbox_merge_{Guid.NewGuid():N}");
        var firstAt = new DateTime(2026, 9, 15, 1, 2, 3, DateTimeKind.Utc);
        var secondAt = firstAt.AddSeconds(1);

        await _storage.EnqueueScoreRecalcAsync(map.MapId, style: 4, track: 2, styleFactor: 1.25, nowUtc: firstAt);

        var outboxMutations = 0;
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsOutboxInsert(sql) || IsOutboxUpdate(sql))
            {
                outboxMutations++;
            }
        };
        await _storage.EnqueueScoreRecalcAsync(map.MapId, style: 4, track: 2, styleFactor: 2.5, nowUtc: secondAt);
        _storage.Db.Aop.OnLogExecuting = null;

        var row = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                   .Where(x => x.MapId == map.MapId && x.Style == 4 && x.Track == 2)
                                   .SingleAsync();

        Assert.Equal(2, row.RequestedGeneration);
        Assert.Equal(0, row.ProcessedGeneration);
        Assert.Equal(2.5, row.StyleFactor);
        Assert.Equal(firstAt.AddSeconds(5), row.AvailableAtUtc);
        Assert.Equal(1, outboxMutations);
        Assert.Equal(1, await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                         .Where(x => x.MapId == map.MapId && x.Style == 4 && x.Track == 2)
                                         .CountAsync());

        Assert.Equal(1, await _storage.ProcessScoreRecalcOutboxBatchAsync(
                         row.AvailableAtUtc, "complete-merged-generation"));
        var thirdAt = secondAt.AddMinutes(1);
        outboxMutations = 0;
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsOutboxInsert(sql) || IsOutboxUpdate(sql))
            {
                outboxMutations++;
            }
        };
        await _storage.EnqueueScoreRecalcAsync(map.MapId, style: 4, track: 2, styleFactor: 3.5, nowUtc: thirdAt);
        _storage.Db.Aop.OnLogExecuting = null;

        var reactivated = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                            .Where(x => x.MapId == map.MapId && x.Style == 4 && x.Track == 2)
                                            .SingleAsync();
        Assert.Equal(3, reactivated.RequestedGeneration);
        Assert.Equal(2, reactivated.ProcessedGeneration);
        Assert.Equal(3.5, reactivated.StyleFactor);
        Assert.Equal(thirdAt.AddSeconds(5), reactivated.AvailableAtUtc);
        Assert.Equal(1, outboxMutations);
    }

    [Fact]
    public async Task ActiveLeaseIsSkippedAndCanBeReclaimedAfterExpiry()
    {
        var map = await _storage.GetMapInfo($"surf_outbox_lease_{Guid.NewGuid():N}");
        var requestedAt = new DateTime(2026, 9, 15, 2, 3, 4, DateTimeKind.Utc);
        await _storage.EnqueueScoreRecalcAsync(map.MapId, style: 3, track: 1, styleFactor: 1,
                                                nowUtc: requestedAt);

        var firstEligibleAt = requestedAt.AddSeconds(5);
        var leaseUntil = firstEligibleAt.AddMinutes(2);
        await _storage.Db.Updateable<ScoreRecalcOutboxEntity>()
                      .SetColumns(x => x.LeaseOwner == "stopped-worker")
                      .SetColumns(x => x.LeaseUntilUtc == leaseUntil)
                      .Where(x => x.MapId == map.MapId)
                      .ExecuteCommandAsync();

        Assert.Equal(0, await _storage.ProcessScoreRecalcOutboxBatchAsync(firstEligibleAt, "early-worker"));
        Assert.Equal(1, await _storage.ProcessScoreRecalcOutboxBatchAsync(leaseUntil, "recovery-worker"));

        var completed = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                         .Where(x => x.MapId == map.MapId)
                                         .SingleAsync();
        Assert.Equal(completed.RequestedGeneration, completed.ProcessedGeneration);
        Assert.Null(completed.LeaseOwner);
        Assert.Null(completed.LeaseUntilUtc);
    }

    [Fact]
    public async Task RepeatedFailuresDeadLetterAndANewGenerationReactivatesTheRow()
    {
        const ulong mapId = 987654321;
        var now = new DateTime(2026, 9, 15, 3, 4, 5, DateTimeKind.Utc);
        await _storage.Db.Insertable(new ScoreRecalcOutboxEntity
        {
            MapId = mapId,
            Style = 0,
            Track = 0,
            RequestedGeneration = 1,
            ProcessedGeneration = 0,
            StyleFactor = 1,
            AvailableAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }).ExecuteCommandAsync();

        ScoreRecalcOutboxEntity failed = null!;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            Assert.Equal(1, await _storage.ProcessScoreRecalcOutboxBatchAsync(now, $"failure-{attempt}"));
            failed = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                      .Where(x => x.MapId == mapId)
                                      .SingleAsync();
            Assert.Equal(attempt, failed.AttemptCount);
            Assert.Null(failed.LeaseOwner);

            if (attempt < 8)
            {
                Assert.Null(failed.DeadLetteredAtUtc);
                Assert.True(failed.AvailableAtUtc > now);
                now = failed.AvailableAtUtc;
            }
        }

        Assert.NotNull(failed.DeadLetteredAtUtc);
        Assert.Contains($"Map {mapId} does not exist", failed.LastError);
        Assert.Equal(0, await _storage.ProcessScoreRecalcOutboxBatchAsync(now.AddHours(1), "ignored-worker"));

        await _storage.Db.Insertable(new MapEntity
        {
            MapId = mapId,
            File = $"surf_outbox_reactivated_{Guid.NewGuid():N}",
            Tier = 1,
        }).OffIdentity().ExecuteCommandAsync();

        var reactivatedAt = now.AddHours(2);
        await _storage.EnqueueScoreRecalcAsync(mapId, style: 0, track: 0, styleFactor: 1.5,
                                                nowUtc: reactivatedAt);

        var reactivated = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                            .Where(x => x.MapId == mapId)
                                            .SingleAsync();
        Assert.Equal(2, reactivated.RequestedGeneration);
        Assert.Equal(0, reactivated.ProcessedGeneration);
        Assert.Equal(0, reactivated.AttemptCount);
        Assert.Null(reactivated.LastError);
        Assert.Null(reactivated.DeadLetteredAtUtc);
        Assert.Equal(reactivatedAt.AddSeconds(5), reactivated.AvailableAtUtc);

        Assert.Equal(1, await _storage.ProcessScoreRecalcOutboxBatchAsync(
                         reactivated.AvailableAtUtc, "reactivated-worker"));
        var completed = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                         .Where(x => x.MapId == mapId)
                                         .SingleAsync();
        Assert.Equal(completed.RequestedGeneration, completed.ProcessedGeneration);
    }

    [Fact]
    public async Task ExpiredDeliveryAtAttemptLimitIsDeadLetteredAfterAWorkerCrash()
    {
        const ulong missingMapId = 987654322;
        var expiredAt = new DateTime(2026, 9, 15, 4, 5, 6, DateTimeKind.Utc);
        await _storage.Db.Insertable(new ScoreRecalcOutboxEntity
        {
            MapId = missingMapId,
            Style = 0,
            Track = 0,
            RequestedGeneration = 1,
            ProcessedGeneration = 0,
            StyleFactor = 1,
            AvailableAtUtc = expiredAt.AddMinutes(-2),
            AttemptCount = 8,
            LeaseOwner = "crashed-worker",
            LeaseUntilUtc = expiredAt,
            CreatedAtUtc = expiredAt.AddHours(-1),
            UpdatedAtUtc = expiredAt.AddMinutes(-2),
        }).ExecuteCommandAsync();

        Assert.Equal(1, await _storage.ProcessScoreRecalcOutboxBatchAsync(expiredAt, "recovery-worker"));

        var deadLettered = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                             .Where(x => x.MapId == missingMapId)
                                             .SingleAsync();
        Assert.Equal(8, deadLettered.AttemptCount);
        Assert.Equal(expiredAt, deadLettered.DeadLetteredAtUtc);
        Assert.Null(deadLettered.LeaseOwner);
        Assert.Null(deadLettered.LeaseUntilUtc);
        Assert.Equal("Lease expired after the maximum delivery attempts.", deadLettered.LastError);
        Assert.Equal(0, deadLettered.ProcessedGeneration);
    }

    [Fact]
    public async Task MainPersonalBestCreatesOutboxButNoRecordAndStageDoNotAdvanceIt()
    {
        var map = await _storage.GetMapInfo($"surf_outbox_record_{Guid.NewGuid():N}");
        var player = new SteamID(76561198000000001);

        var first = await _storage.AddPlayerRecord(player, map.MapName, new RecordRequest
        {
            Time = 80,
            StyleFactor = 1.75,
        });
        Assert.Equal(EAttemptResult.NewServerRecord, first.Item1);

        var outboxAfterFirst = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                                .Where(x => x.MapId == map.MapId && x.Style == 0 && x.Track == 0)
                                                .SingleAsync();
        Assert.Equal(1, outboxAfterFirst.RequestedGeneration);

        var noRecord = await _storage.AddPlayerRecord(player, map.MapName, new RecordRequest
        {
            Time = 90,
            StyleFactor = 3.0,
        });
        Assert.Equal(EAttemptResult.NoNewRecord, noRecord.Item1);

        var stage = await _storage.AddPlayerStageRecord(player, map.MapName, new RecordRequest
        {
            Stage = 1,
            Time = 50,
            StyleFactor = 4.0,
        });
        Assert.Equal(EAttemptResult.NewServerRecord, stage.Item1);

        var rows = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                    .Where(x => x.MapId == map.MapId)
                                    .ToListAsync();
        var outbox = Assert.Single(rows);
        Assert.Equal(1, outbox.RequestedGeneration);
        Assert.Equal(1.75, outbox.StyleFactor);
        Assert.Equal(3, await _storage.Db.Queryable<RunEntity>().Where(x => x.MapId == map.MapId).CountAsync());
        Assert.Equal(1, await _storage.Db.Queryable<RunEntity>()
                                         .Where(x => x.MapId == map.MapId && x.RunType == RunType.Stage)
                                         .CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainRunBestCheckpointAndOutboxRollbackTogetherWhenOutboxWriteFails(bool failUpdate)
    {
        var map = await _storage.GetMapInfo($"surf_outbox_rollback_{Guid.NewGuid():N}");
        var player = new SteamID(76561198000000002);

        if (failUpdate)
        {
            await _storage.EnqueueScoreRecalcAsync(map.MapId, style: 0, track: 0, styleFactor: 1.0,
                                                    nowUtc: new DateTime(2026, 9, 15, 1, 2, 3, DateTimeKind.Utc));
        }

        var runsBefore = await _storage.Db.Queryable<RunEntity>().Where(x => x.MapId == map.MapId).CountAsync();
        var bestBefore = await _storage.Db.Queryable<PlayerBestRunEntity>().Where(x => x.MapId == map.MapId).CountAsync();
        var segmentsBefore = await _storage.Db.Queryable<RunSegmentEntity>()
                                               .Where(x => x.RunId > 0)
                                               .CountAsync();
        var outboxBefore = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                             .Where(x => x.MapId == map.MapId)
                                             .ToListAsync();

        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if ((!failUpdate && IsOutboxInsert(sql)) || (failUpdate && IsOutboxUpdate(sql)))
            {
                throw new InvalidOperationException("Injected score-recalc outbox write failure");
            }
        };

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => _storage.AddPlayerRecord(player, map.MapName, new RecordRequest
            {
                Time = 70,
                Checkpoints =
                [
                    new RecordRequest.CheckpointRecord { CheckpointIndex = 1, Time = 35 },
                ],
            }));
        }
        finally
        {
            _storage.Db.Aop.OnLogExecuting = null;
        }

        Assert.Equal(runsBefore, await _storage.Db.Queryable<RunEntity>().Where(x => x.MapId == map.MapId).CountAsync());
        Assert.Equal(bestBefore, await _storage.Db.Queryable<PlayerBestRunEntity>().Where(x => x.MapId == map.MapId).CountAsync());
        Assert.Equal(segmentsBefore, await _storage.Db.Queryable<RunSegmentEntity>().Where(x => x.RunId > 0).CountAsync());

        var outboxAfter = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                            .Where(x => x.MapId == map.MapId)
                                            .ToListAsync();
        Assert.Equal(outboxBefore.Count, outboxAfter.Count);
        if (failUpdate)
        {
            var before = Assert.Single(outboxBefore);
            var after = Assert.Single(outboxAfter);
            Assert.Equal(before.RequestedGeneration, after.RequestedGeneration);
            Assert.Equal(before.StyleFactor, after.StyleFactor);
        }
        else
        {
            Assert.Empty(outboxAfter);
        }
    }

    [Fact]
    public void InitCreatesOutboxTableAndDeclaredIndexes()
    {
        Assert.True(_storage.Db.DbMaintenance.IsAnyTable("surf_score_recalc_outbox", false));
        Assert.True(_storage.Db.DbMaintenance.IsAnyIndex("idx_score_recalc_outbox_unique"));
        Assert.True(_storage.Db.DbMaintenance.IsAnyIndex("idx_score_recalc_outbox_pending"));
    }

    [Fact]
    public async Task InitStartsWorkerWhichRecoversAnExistingDurableRequest()
    {
        var workerPath = Path.Combine(Path.GetTempPath(), $"timer-sql-outbox-worker-{Guid.NewGuid():N}.db");
        StorageServiceImpl? setup = null;
        StorageServiceImpl? worker = null;

        try
        {
            setup = CreateStorage(workerPath, enableWorker: false);
            setup.Init();
            var map = await setup.GetMapInfo($"surf_outbox_worker_{Guid.NewGuid():N}");
            var availableAt = DateTime.UtcNow.AddMinutes(-1);
            await setup.Db.Insertable(new ScoreRecalcOutboxEntity
            {
                MapId = map.MapId,
                Style = 0,
                Track = 0,
                RequestedGeneration = 1,
                ProcessedGeneration = 0,
                StyleFactor = 1,
                AvailableAtUtc = availableAt,
                CreatedAtUtc = availableAt,
                UpdatedAtUtc = availableAt,
            }).ExecuteCommandAsync();
            setup.Shutdown();
            setup = null;

            worker = CreateStorage(workerPath, enableWorker: true);
            worker.Init(initializeSchema: false);

            var deadline = DateTime.UtcNow.AddSeconds(5);
            ScoreRecalcOutboxEntity row;
            do
            {
                row = await worker.Db.Queryable<ScoreRecalcOutboxEntity>().SingleAsync();
                if (row.ProcessedGeneration == row.RequestedGeneration)
                {
                    break;
                }

                await Task.Delay(20);
            }
            while (DateTime.UtcNow < deadline);

            Assert.Equal(row.RequestedGeneration, row.ProcessedGeneration);
            Assert.Null(row.LeaseOwner);
        }
        finally
        {
            setup?.Shutdown();
            worker?.Shutdown();
            try
            {
                File.Delete(workerPath);
            }
            catch (IOException)
            {
            }
        }
    }

    private static StorageServiceImpl CreateStorage(string path, bool enableWorker)
    {
        var storage = new StorageServiceImpl(DbType.Sqlite,
                                             $"Data Source={path};Pooling=False",
                                             NullLogger<StorageServiceImpl>.Instance,
                                             enableScoreRecalcWorker: enableWorker);
        storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        return storage;
    }

    private static bool IsOutboxInsert(string sql)
        => sql.Contains("surf_score_recalc_outbox", StringComparison.OrdinalIgnoreCase)
           && sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);

    private static bool IsOutboxUpdate(string sql)
        => sql.Contains("surf_score_recalc_outbox", StringComparison.OrdinalIgnoreCase)
           && sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _storage.Shutdown();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // SQLite may still hold a transient handle on a failed test; leave the
            // uniquely named temp file for the OS cleanup rather than masking a test error.
        }
    }
}
