using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

public sealed class ScoreAdministrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-score-administration-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;

    public ScoreAdministrationTests()
    {
        _storage = new StorageServiceImpl(DbType.Sqlite,
                                          $"Data Source={_path};Pooling=False",
                                          NullLogger<StorageServiceImpl>.Instance,
                                          enableScoreRecalcWorker: false);
        _storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity)
            {
                column.DataType = "INTEGER";
            }
        };
        _storage.Init();
    }

    [Fact]
    public async Task SetTierUpdatesTierAndQueuesConfiguredBoardsInOneMapTransaction()
    {
        var map = await _storage.GetMapInfo($"surf_score_admin_{Guid.NewGuid():N}");
        await AddMainRunAsync(map.MapId, style: 0);
        await AddMainRunAsync(map.MapId, style: 2);
        var policy = new Dictionary<int, double> { [0] = 1, [2] = 1.5 };

        var result = await _storage.SetMapTierAndRequeueScoresAsync(map.MapName, tier: 3, policy);

        Assert.True(result.MapFound);
        Assert.Equal(1, result.MapsAffected);
        Assert.Equal((byte?)1, result.PreviousTier);
        Assert.Equal((byte?)3, result.CurrentTier);
        Assert.Equal(2, result.BoardsQueued);
        Assert.Equal(0, result.DeadLettersRequeued);

        var storedMap = await _storage.Db.Queryable<MapEntity>()
                                      .Where(x => x.MapId == map.MapId)
                                      .SingleAsync();
        Assert.Equal(3, storedMap.Tier);

        var queued = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                   .Where(x => x.MapId == map.MapId)
                                   .OrderBy(x => x.Style)
                                   .ToListAsync();
        Assert.Equal(2, queued.Count);
        Assert.Collection(queued,
                          row =>
                          {
                              Assert.Equal(0, row.Style);
                              Assert.Equal(1, row.StyleFactor);
                          },
                          row =>
                          {
                              Assert.Equal(2, row.Style);
                              Assert.Equal(1.5, row.StyleFactor);
                          });
    }

    [Fact]
    public async Task SetTierQueuesOnlyMainTrackBoards()
    {
        var map = await _storage.GetMapInfo($"surf_score_admin_main_only_{Guid.NewGuid():N}");
        await AddMainRunAsync(map.MapId, style: 0, track: 0);
        await AddMainRunAsync(map.MapId, style: 0, track: 1);

        var result = await _storage.SetMapTierAndRequeueScoresAsync(
            map.MapName, tier: 3, new Dictionary<int, double> { [0] = 1 });

        Assert.Equal(1, result.BoardsQueued);
        var queued = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                   .Where(x => x.MapId == map.MapId)
                                   .SingleAsync();
        Assert.Equal((ushort)0, queued.Track);
    }

    [Fact]
    public async Task SetTierRollsBackWhenItsDurableOutboxWriteFails()
    {
        var map = await _storage.GetMapInfo($"surf_score_admin_rollback_{Guid.NewGuid():N}");
        await AddMainRunAsync(map.MapId, style: 0);
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("surf_score_recalc_outbox", StringComparison.OrdinalIgnoreCase)
                && sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Injected administrative outbox failure");
            }
        };

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => _storage.SetMapTierAndRequeueScoresAsync(
                map.MapName, tier: 3, new Dictionary<int, double> { [0] = 1 }));
        }
        finally
        {
            _storage.Db.Aop.OnLogExecuting = null;
        }

        var storedMap = await _storage.Db.Queryable<MapEntity>()
                                      .Where(x => x.MapId == map.MapId)
                                      .SingleAsync();
        Assert.Equal(1, storedMap.Tier);
        Assert.Empty(await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                      .Where(x => x.MapId == map.MapId)
                                      .ToListAsync());
    }

    [Fact]
    public async Task RequeuePolicyReactivatesDeadLetterUsingCurrentFactor()
    {
        var map = await _storage.GetMapInfo($"surf_score_admin_dead_{Guid.NewGuid():N}");
        var failedAt = new DateTime(2026, 9, 16, 1, 2, 3, DateTimeKind.Utc);
        await _storage.Db.Insertable(new ScoreRecalcOutboxEntity
        {
            MapId = map.MapId,
            Style = 0,
            Track = 0,
            RequestedGeneration = 7,
            ProcessedGeneration = 6,
            StyleFactor = 0.25,
            AvailableAtUtc = failedAt,
            AttemptCount = 8,
            LeaseOwner = "stale-dead-letter-lease",
            LeaseUntilUtc = failedAt.AddHours(1),
            LastError = "Old worker failure",
            DeadLetteredAtUtc = failedAt,
            CreatedAtUtc = failedAt.AddMinutes(-5),
            UpdatedAtUtc = failedAt,
        }).ExecuteCommandAsync();

        var result = await _storage.RequeueMapScorePolicyAsync(
            map.MapName, new Dictionary<int, double> { [0] = 2 });

        Assert.True(result.MapFound);
        Assert.Equal(1, result.BoardsQueued);
        Assert.Equal(1, result.DeadLettersRequeued);
        var reactivated = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                        .Where(x => x.MapId == map.MapId)
                                        .SingleAsync();
        Assert.Equal(8, reactivated.RequestedGeneration);
        Assert.Equal(6, reactivated.ProcessedGeneration);
        Assert.Equal(2, reactivated.StyleFactor);
        Assert.Equal(0, reactivated.AttemptCount);
        Assert.Null(reactivated.LeaseOwner);
        Assert.Null(reactivated.LeaseUntilUtc);
        Assert.Null(reactivated.LastError);
        Assert.Null(reactivated.DeadLetteredAtUtc);
    }

    [Fact]
    public async Task RequeuePolicyLeavesUnconfiguredDeadLetterVisibleForOperatorAction()
    {
        var map = await _storage.GetMapInfo($"surf_score_admin_disabled_{Guid.NewGuid():N}");
        var failedAt = new DateTime(2026, 9, 16, 1, 2, 3, DateTimeKind.Utc);
        await _storage.Db.Insertable(new ScoreRecalcOutboxEntity
        {
            MapId = map.MapId,
            Style = 7,
            Track = 0,
            RequestedGeneration = 1,
            ProcessedGeneration = 0,
            StyleFactor = 1,
            AvailableAtUtc = failedAt,
            DeadLetteredAtUtc = failedAt,
            CreatedAtUtc = failedAt,
            UpdatedAtUtc = failedAt,
        }).ExecuteCommandAsync();

        var result = await _storage.RequeueMapScorePolicyAsync(
            map.MapName, new Dictionary<int, double> { [0] = 1 });

        Assert.Equal(0, result.BoardsQueued);
        Assert.Equal(0, result.DeadLettersRequeued);
        Assert.Equal(1, result.DisabledStyleBoardsSkipped);
        var unchanged = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                      .Where(x => x.MapId == map.MapId)
                                      .SingleAsync();
        Assert.Equal(failedAt, unchanged.DeadLetteredAtUtc);
        Assert.Equal(1, unchanged.RequestedGeneration);
    }

    [Fact]
    public async Task SetTierRejectsAnUnrepresentableConfiguredMainScoreBeforeUpdatingMap()
    {
        var map = await _storage.GetMapInfo($"surf_score_admin_overflow_{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _storage.SetMapTierAndRequeueScoresAsync(
            map.MapName, tier: 27, new Dictionary<int, double> { [0] = 1 }));

        var storedMap = await _storage.Db.Queryable<MapEntity>()
                                      .Where(x => x.MapId == map.MapId)
                                      .SingleAsync();
        Assert.Equal(1, storedMap.Tier);
        Assert.Empty(await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                      .Where(x => x.MapId == map.MapId)
                                      .ToListAsync());
    }

    [Fact]
    public async Task RecalcScoresRejectsAPolicyThatCannotBeRepresentedBeforeQueueing()
    {
        var map = await _storage.GetMapInfo($"surf_score_admin_recalc_overflow_{Guid.NewGuid():N}");
        await AddMainRunAsync(map.MapId, style: 0);
        // A tier written outside set-tier (e.g. the plugin's local set_tier) skips that preflight.
        await _storage.Db.Updateable<MapEntity>()
                      .SetColumns(x => x.Tier == 27)
                      .Where(x => x.MapId == map.MapId)
                      .ExecuteCommandAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _storage.RequeueMapScorePolicyAsync(
            map.MapName, new Dictionary<int, double> { [0] = 1 }));
        Assert.Empty(await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                      .Where(x => x.MapId == map.MapId)
                                      .ToListAsync());

        // A zero factor removes the board's points and is always representable.
        var result = await _storage.RequeueMapScorePolicyAsync(map.MapName, new Dictionary<int, double> { [0] = 0 });
        Assert.Equal(1, result.BoardsQueued);
    }

    [Fact]
    public async Task SetTierIsNotBlockedWhenOnlyAPlayersCrossBoardTotalWouldOverflow()
    {
        var map = await _storage.GetMapInfo($"surf_score_admin_total_overflow_{Guid.NewGuid():N}");
        await AddMainRunAsync(map.MapId, style: 0);
        await AddMainRunAsync(map.MapId, style: 1);

        // Tier 26 keeps each main-style rank-one score below UInt32.MaxValue, but the same
        // player's two boards sum past it. The score worker caps that total, so the tier change
        // must be accepted (rejecting it would also block set-tier on every other map this
        // player has a score on).
        var result = await _storage.SetMapTierAndRequeueScoresAsync(
            map.MapName, tier: 26, new Dictionary<int, double> { [0] = 1, [1] = 1 });

        Assert.Equal<byte?>(26, result.CurrentTier);
        Assert.Equal(2, result.BoardsQueued);
    }

    [Fact]
    public async Task SetTierUsesActualLowRanksInsteadOfTreatingEveryPlayerAsRankOne()
    {
        var map = await _storage.GetMapInfo($"surf_score_admin_low_rank_{Guid.NewGuid():N}");
        const long targetPlayer = 100;

        // The target player participates in both styles but is third in each; the leaders are
        // different players, so the exact post-tier total is representable. A rank-one ceiling
        // for both boards would incorrectly reject this tier-26 operation.
        await AddMainRunAsync(map.MapId, style: 0, steamId: 1, time: 10);
        await AddMainRunAsync(map.MapId, style: 0, steamId: 2, time: 20);
        await AddMainRunAsync(map.MapId, style: 0, steamId: targetPlayer, time: 30);
        await AddMainRunAsync(map.MapId, style: 1, steamId: 3, time: 10);
        await AddMainRunAsync(map.MapId, style: 1, steamId: 4, time: 20);
        await AddMainRunAsync(map.MapId, style: 1, steamId: targetPlayer, time: 30);

        var result = await _storage.SetMapTierAndRequeueScoresAsync(
            map.MapName, tier: 26, new Dictionary<int, double> { [0] = 1, [1] = 1 });

        Assert.True(result.MapFound);
        Assert.Equal(2, result.BoardsQueued);
        var storedMap = await _storage.Db.Queryable<MapEntity>()
                                      .Where(x => x.MapId == map.MapId)
                                      .SingleAsync();
        Assert.Equal(26, storedMap.Tier);
    }

    [Fact]
    public async Task RequeueAllAppliesCurrentPolicyToEveryMapIndependently()
    {
        var first = await _storage.GetMapInfo($"surf_score_admin_all_a_{Guid.NewGuid():N}");
        var second = await _storage.GetMapInfo($"surf_score_admin_all_b_{Guid.NewGuid():N}");
        await AddMainRunAsync(first.MapId, style: 0);
        await AddMainRunAsync(second.MapId, style: 0);

        var result = await _storage.RequeueAllScorePoliciesAsync(new Dictionary<int, double> { [0] = 0 });

        Assert.Equal(2, result.MapsAffected);
        Assert.Equal(2, result.BoardsQueued);
        var rows = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                    .OrderBy(x => x.MapId)
                                    .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(0, row.StyleFactor));
    }

    [Fact]
    public async Task RequeueAllContinuesPastAMapWhosePolicyCannotBeRepresented()
    {
        var bad = await _storage.GetMapInfo($"surf_score_admin_all_0bad_{Guid.NewGuid():N}");
        var good = await _storage.GetMapInfo($"surf_score_admin_all_zgood_{Guid.NewGuid():N}");
        await AddMainRunAsync(bad.MapId, style: 0);
        await AddMainRunAsync(good.MapId, style: 0);
        await _storage.Db.Updateable<MapEntity>()
                      .SetColumns(x => x.Tier == 27)
                      .Where(x => x.MapId == bad.MapId)
                      .ExecuteCommandAsync();

        var result = await _storage.RequeueAllScorePoliciesAsync(new Dictionary<int, double> { [0] = 1 });

        // The bad map (sorted first) is reported by name; the good map is still requeued.
        var failure = Assert.Single(result.FailedMaps);
        Assert.Contains(bad.MapName, failure);
        Assert.Equal(1, result.MapsAffected);
        Assert.Single(await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                       .Where(x => x.MapId == good.MapId)
                                       .ToListAsync());
        Assert.Empty(await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                      .Where(x => x.MapId == bad.MapId)
                                      .ToListAsync());
    }

    private Task AddMainRunAsync(ulong mapId,
                                 int style,
                                 long steamId = 0,
                                 float time = 80,
                                 ushort track = 0)
        => _storage.Db.Insertable(new RunEntity
        {
            MapId = mapId,
            SteamId = steamId,
            RunType = RunType.Main,
            Stage = 0,
            Style = style,
            Track = track,
            Time = time,
        }).ExecuteCommandAsync();

    public void Dispose()
    {
        _storage.Shutdown();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // SQLite can keep a transient handle after a failed test. The path is unique.
        }
    }
}
