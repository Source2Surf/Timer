using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;
using Timer.RequestManager.Backend;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

public sealed class RunSubmissionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-run-submission-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;

    public RunSubmissionTests()
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

    [Fact]
    public async Task FirstSubmissionAndExactRetryHaveOneDurableResult()
    {
        var map = await _storage.GetMapInfo($"surf_submission_first_{Guid.NewGuid():N}");
        const long steamId = 76561198000000001;
        await EnsurePlayerAsync(steamId);
        var submissionId = Guid.NewGuid();
        var submissionIdText = submissionId.ToString("N");
        var command = CreateCommand(map.MapName, steamId, submissionId, timeMicros: 80_000_000);

        var accepted = await _storage.SubmitBackendRunAsync(command);
        var retry = await _storage.SubmitBackendRunAsync(CreateCommand(map.MapName, steamId, submissionId,
                                                                         timeMicros: 80_000_000, styleFactor: 7.5));

        Assert.Equal(TimerBackendSubmissionDisposition.Accepted, accepted.Disposition);
        Assert.Equal(TimerBackendAttemptResult.NewServerRecord, accepted.AttemptResult);
        Assert.Equal(TimerBackendRankState.Ready, accepted.RankState);
        Assert.Equal(1, accepted.Rank);
        Assert.Equal(TimerBackendSubmissionDisposition.AlreadyApplied, retry.Disposition);
        Assert.Equal(accepted.RunId, retry.RunId);
        Assert.Equal(accepted.AttemptResult, retry.AttemptResult);
        var savedRuns = await _storage.Db.Queryable<RunEntity>().ToListAsync();
        Assert.True(savedRuns.Count == 1,
                    $"Expected one run; found {savedRuns.Count}: {string.Join(", ", savedRuns.Select(x => x.Id))}");
        Assert.Equal(accepted.RunId, savedRuns[0].Id);
        Assert.False(_storage.Db.DbMaintenance.IsAnyColumn("surf_runs", "SubmissionId", false));
        Assert.False(_storage.Db.DbMaintenance.IsAnyIndex("idx_surf_runs_submission_unique"));
        Assert.Equal(2, await _storage.Db.Queryable<RunSegmentEntity>().Where(x => x.RunId == accepted.RunId).CountAsync());

        var inboxes = await _storage.Db.Queryable<RunSubmissionEntity>().ToListAsync();
        Assert.True(inboxes.Count == 1,
                    $"Expected one inbox; found {inboxes.Count}: {string.Join(", ", inboxes.Select(x => x.SubmissionId))}");
        var inbox = inboxes[0];
        Assert.Equal(submissionIdText, inbox.SubmissionId);
        Assert.Equal(1, inbox.HashVersion);
        Assert.Matches("^[0-9a-f]{64}$", inbox.PayloadHash);
        Assert.Equal(accepted.RunId, inbox.RunId);
        Assert.Equal(1, await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                      .Where(x => x.MapId == map.MapId && x.Style == command.Style && x.Track == command.Track)
                                      .CountAsync());

        var status = await _storage.GetBackendSubmissionStatusAsync(submissionId);
        Assert.NotNull(status);
        Assert.Equal(TimerBackendSubmissionDisposition.AlreadyApplied, status!.Disposition);
        Assert.Equal(accepted.RunId, status.RunId);
        Assert.Equal(DateTimeKind.Utc, status.ReceivedAtUtc.Kind);
    }

    [Fact]
    public async Task SameSubmissionIdWithDifferentCanonicalPayloadPermanentlyConflicts()
    {
        var map = await _storage.GetMapInfo($"surf_submission_conflict_{Guid.NewGuid():N}");
        const long steamId = 76561198000000002;
        await EnsurePlayerAsync(steamId);
        var id = Guid.NewGuid();
        var idText = id.ToString("N");
        var accepted = await _storage.SubmitBackendRunAsync(CreateCommand(map.MapName, steamId, id));

        await Assert.ThrowsAsync<TimerBackendSubmissionConflictException>(() => _storage.SubmitBackendRunAsync(
            CreateCommand(map.MapName, steamId, id, timeMicros: 79_000_000)));

        Assert.Equal(1, await _storage.Db.Queryable<RunSubmissionEntity>()
                                      .Where(x => x.SubmissionId == idText)
                                      .CountAsync());
        Assert.Equal(1, await _storage.Db.Queryable<RunEntity>()
                                      .Where(x => x.Id == accepted.RunId)
                                      .CountAsync());
        Assert.Equal(1, await _storage.Db.Queryable<RunEntity>().CountAsync());
    }

    [Fact]
    public async Task InboxFinalizeFailureRollsBackRunSegmentsBestOutboxAndReservation()
    {
        var map = await _storage.GetMapInfo($"surf_submission_rollback_{Guid.NewGuid():N}");
        const long steamId = 76561198000000003;
        await EnsurePlayerAsync(steamId);
        var id = Guid.NewGuid();
        var idText = id.ToString("N");
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("surf_run_submissions", StringComparison.OrdinalIgnoreCase)
                && sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Injected inbox finalization failure");
            }
        };

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => _storage.SubmitBackendRunAsync(
                CreateCommand(map.MapName, steamId, id, style: 5, track: 2)));
        }
        finally
        {
            _storage.Db.Aop.OnLogExecuting = null;
        }

        Assert.Equal(0, await _storage.Db.Queryable<RunSubmissionEntity>()
                                      .Where(x => x.SubmissionId == idText)
                                      .CountAsync());
        Assert.Equal(0, await _storage.Db.Queryable<RunEntity>()
                                      .CountAsync());
        Assert.Equal(0, await _storage.Db.Queryable<RunSegmentEntity>().CountAsync());
        Assert.Equal(0, await _storage.Db.Queryable<PlayerBestRunEntity>()
                                      .Where(x => x.MapId == map.MapId && x.Style == 5 && x.Track == 2)
                                      .CountAsync());
        Assert.Equal(0, await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                      .Where(x => x.MapId == map.MapId && x.Style == 5 && x.Track == 2)
                                      .CountAsync());
    }

    [Fact]
    public async Task MissingMapAndPlayerAreExplicitDomainFailures()
    {
        const long steamId = 76561198000000004;
        await Assert.ThrowsAsync<TimerBackendMapNotFoundException>(() => _storage.SubmitBackendRunAsync(
            CreateCommand($"surf_submission_missing_{Guid.NewGuid():N}", steamId, Guid.NewGuid())));

        var map = await _storage.GetMapInfo($"surf_submission_player_{Guid.NewGuid():N}");
        await Assert.ThrowsAsync<TimerBackendPlayerNotFoundException>(() => _storage.SubmitBackendRunAsync(
            CreateCommand(map.MapName, steamId, Guid.NewGuid())));
        Assert.Equal(0, await _storage.Db.Queryable<RunSubmissionEntity>().CountAsync());
    }

    [Fact]
    public async Task EnsuringAPlayerProfileCreatesThenUpdatesItAndAllowsSubmission()
    {
        var map = await _storage.GetMapInfo($"surf_submission_profile_{Guid.NewGuid():N}");
        const long steamId = 76561198000001004;

        var created = await _storage.EnsureBackendPlayerProfileAsync(new TimerBackendPlayerProfileCommand
        {
            SteamId = steamId,
            Name = "First Profile Name",
        });
        var updated = await _storage.EnsureBackendPlayerProfileAsync(new TimerBackendPlayerProfileCommand
        {
            SteamId = steamId,
            Name = "Updated Profile Name",
        });

        Assert.True(created.PlayerId > 0);
        Assert.Equal(created.PlayerId, updated.PlayerId);
        Assert.Equal("Updated Profile Name", updated.Name);
        Assert.Equal(0U, updated.Points);
        Assert.Equal(DateTimeKind.Utc, updated.JoinDateUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, updated.LastSeenDateUtc.Kind);
        Assert.Equal(1, await _storage.Db.Queryable<PlayerEntity>()
                                      .Where(x => x.SteamId == steamId)
                                      .CountAsync());
        Assert.Equal("Updated Profile Name", await _storage.Db.Queryable<PlayerEntity>()
                                                        .Where(x => x.SteamId == steamId)
                                                        .Select(x => x.Name)
                                                        .SingleAsync());

        var result = await _storage.SubmitBackendRunAsync(
            CreateCommand(map.MapName, steamId, Guid.NewGuid()));
        Assert.Equal(TimerBackendSubmissionDisposition.Accepted, result.Disposition);
    }

    [Theory]
    [InlineData(0, "Player")]
    [InlineData(-1, "Player")]
    [InlineData(76561198000000001L, "")]
    [InlineData(76561198000000001L, " Player")]
    [InlineData(76561198000000001L, "Player\nName")]
    public async Task EnsuringAPlayerProfileRejectsInvalidBoundaryValues(long steamId, string name)
    {
        await Assert.ThrowsAsync<TimerBackendPlayerProfileValidationException>(() =>
            _storage.EnsureBackendPlayerProfileAsync(new TimerBackendPlayerProfileCommand
            {
                SteamId = steamId,
                Name = name,
            }));
    }

    [Fact]
    public async Task MainPersonalBestIsPendingAndStageDoesNotAdvanceMainOutbox()
    {
        var map = await _storage.GetMapInfo($"surf_submission_main_stage_{Guid.NewGuid():N}");
        const long firstSteamId = 76561198000000005;
        const long secondSteamId = 76561198000000006;
        await EnsurePlayerAsync(firstSteamId);
        await EnsurePlayerAsync(secondSteamId);

        var first = await _storage.SubmitBackendRunAsync(CreateCommand(map.MapName, firstSteamId, Guid.NewGuid(),
                                                                         style: 4, track: 1, timeMicros: 80_000_000));
        var personalBest = await _storage.SubmitBackendRunAsync(CreateCommand(map.MapName, secondSteamId, Guid.NewGuid(),
                                                                                style: 4, track: 1, timeMicros: 90_000_000));
        var noNewRecord = await _storage.SubmitBackendRunAsync(CreateCommand(map.MapName, secondSteamId, Guid.NewGuid(),
                                                                              style: 4, track: 1, timeMicros: 100_000_000));
        var stage = await _storage.SubmitBackendRunAsync(CreateCommand(map.MapName, firstSteamId, Guid.NewGuid(),
                                                                        kind: TimerBackendRunKind.Stage, stage: 1,
                                                                        style: 4, track: 1, timeMicros: 30_000_000));

        Assert.Equal(TimerBackendAttemptResult.NewServerRecord, first.AttemptResult);
        Assert.Equal(TimerBackendAttemptResult.NewPersonalRecord, personalBest.AttemptResult);
        Assert.Equal(TimerBackendRankState.Pending, personalBest.RankState);
        Assert.Equal(0, personalBest.Rank);
        Assert.Equal(TimerBackendAttemptResult.NoNewRecord, noNewRecord.AttemptResult);
        Assert.Equal(TimerBackendRankState.NotApplicable, noNewRecord.RankState);
        Assert.Equal(0, noNewRecord.Rank);
        Assert.Equal(TimerBackendAttemptResult.NewServerRecord, stage.AttemptResult);
        Assert.Equal(TimerBackendRankState.Ready, stage.RankState);
        Assert.Equal(1, stage.Rank);

        var mainOutbox = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                      .Where(x => x.MapId == map.MapId && x.Style == 4 && x.Track == 1)
                                      .SingleAsync();
        Assert.Equal(2, mainOutbox.RequestedGeneration);
        Assert.Equal(3, await _storage.Db.Queryable<RunEntity>()
                                      .Where(x => x.MapId == map.MapId && x.RunType == RunType.Main)
                                      .CountAsync());
        Assert.Equal(1, await _storage.Db.Queryable<RunEntity>()
                                      .Where(x => x.MapId == map.MapId && x.RunType == RunType.Stage && x.Stage == 1)
                                      .CountAsync());
    }

    [Fact]
    public async Task HistoricalRunsAreSeededBeforeBackendAttemptResolution()
    {
        var map = await _storage.GetMapInfo($"surf_submission_seed_{Guid.NewGuid():N}");
        const long steamId = 76561198000000007;
        const int style = 7;
        const ushort track = 3;
        await EnsurePlayerAsync(steamId);

        var historical = new RunEntity
        {
            MapId = map.MapId,
            SteamId = steamId,
            RunType = RunType.Main,
            Style = style,
            Track = track,
            Stage = 0,
            Time = 60,
            DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow.AddDays(-1)),
        };
        historical.Id = unchecked((ulong)await _storage.Db.Insertable(historical)
                                                        .ExecuteReturnBigIdentityAsync());

        var result = await _storage.SubmitBackendRunAsync(CreateCommand(
            map.MapName,
            steamId,
            Guid.NewGuid(),
            style: style,
            track: track,
            timeMicros: 80_000_000));

        Assert.Equal(TimerBackendAttemptResult.NoNewRecord, result.AttemptResult);
        var best = await _storage.Db.Queryable<PlayerBestRunEntity>()
                                 .Where(x => x.MapId == map.MapId
                                             && x.SteamId == steamId
                                             && x.RunType == RunType.Main
                                             && x.Style == style
                                             && x.Track == track
                                             && x.Stage == 0)
                                 .SingleAsync();
        Assert.Equal(historical.Id, best.RunId);
        Assert.Equal(60, best.BestTime);
        Assert.Equal(0, await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                      .Where(x => x.MapId == map.MapId && x.Style == style && x.Track == track)
                                      .CountAsync());
    }

    [Fact]
    public async Task CanonicalPayloadHashV1HasStableBinaryEncoding()
    {
        var map = await _storage.GetMapInfo("surf_submission_hash");
        const long steamId = 76561198000000008;
        await EnsurePlayerAsync(steamId);

        var result = await _storage.SubmitBackendRunAsync(
            CreateCommand(map.MapName, steamId, Guid.NewGuid()));
        var inbox = await _storage.Db.Queryable<RunSubmissionEntity>()
                                  .Where(x => x.RunId == result.RunId)
                                  .SingleAsync();

        Assert.Equal("d63345a1bafa2860ca5554f86f462a91ace83c97ffd7ce60da4438140951a8c8",
                     inbox.PayloadHash);
    }

    [Fact]
    public async Task StatusNeverTreatsAnUnfinishedInboxReservationAsCommitted()
    {
        var submissionId = Guid.NewGuid();
        await _storage.Db.Insertable(new RunSubmissionEntity
        {
            SubmissionId = submissionId.ToString("N"),
            PayloadHash = new string('0', 64),
            HashVersion = 1,
            ContractVersion = 1,
            RulesetVersion = 1,
            RunId = 0,
            FinishedAtUtc = DateTime.UtcNow,
            ReceivedAtUtc = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _storage.GetBackendSubmissionStatusAsync(submissionId));
    }

    [Fact]
    public async Task BackendZeroStyleFactorStoresRunButAwardsNoPoints()
    {
        var map = await _storage.GetMapInfo($"surf_submission_no_points_{Guid.NewGuid():N}");
        const long steamId = 76561198000000063;
        await EnsurePlayerAsync(steamId);

        var accepted = await _storage.SubmitBackendRunAsync(
            CreateCommand(map.MapName, steamId, Guid.NewGuid(), styleFactor: 0));
        Assert.True(accepted.RunId > 0);

        var outbox = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>()
                                   .Where(x => x.MapId == map.MapId && x.Style == 2 && x.Track == 0)
                                   .SingleAsync();
        Assert.Equal(0, outbox.StyleFactor);
        Assert.Equal(1, await _storage.ProcessScoreRecalcOutboxBatchAsync(
            outbox.AvailableAtUtc, "zero-factor-worker"));
        Assert.Equal(0, await _storage.Db.Queryable<PlayerTrackScoreEntity>()
                                         .Where(x => x.MapId == map.MapId && x.Style == 2 && x.Track == 0)
                                         .CountAsync());
        var player = await _storage.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == steamId).SingleAsync();
        Assert.Equal(0u, player.Points);
    }

    [Fact]
    public async Task ChangedPolicyAcknowledgesOnlyAnExactCommittedRetry()
    {
        var map = await _storage.GetMapInfo($"surf_submission_policy_{Guid.NewGuid():N}");
        const long steamId = 76561198000000011;
        await EnsurePlayerAsync(steamId);
        var submissionId = Guid.NewGuid();
        var original = CreateCommand(map.MapName, steamId, submissionId, style: 5);
        var accepted = await _storage.SubmitBackendRunAsync(original);

        // The backend may have changed its ruleset version or disabled style 5 since the
        // original response was lost. The immutable old payload must still be acknowledged.
        var replay = await _storage.SubmitBackendRunAsync(
            CreateCommand(map.MapName, steamId, submissionId, style: 5, styleFactor: 1),
            acceptNewWrites: false);
        Assert.Equal(TimerBackendSubmissionDisposition.AlreadyApplied, replay.Disposition);
        Assert.Equal(accepted.RunId, replay.RunId);

        await Assert.ThrowsAsync<TimerBackendSubmissionConflictException>(() =>
            _storage.SubmitBackendRunAsync(
                CreateCommand(map.MapName, steamId, submissionId, style: 5, timeMicros: 79_000_000),
                acceptNewWrites: false));
        await Assert.ThrowsAsync<TimerBackendSubmissionPolicyException>(() =>
            _storage.SubmitBackendRunAsync(
                CreateCommand(map.MapName, steamId, Guid.NewGuid(), style: 5),
                acceptNewWrites: false));

        Assert.Equal(1, await _storage.Db.Queryable<RunEntity>()
                                      .Where(x => x.MapId == map.MapId).CountAsync());
        Assert.Equal(1, await _storage.Db.Queryable<RunSubmissionEntity>().CountAsync());
    }

    [Fact]
    public async Task CancelledSubmissionRollsBackEveryWriteAndCanRetryTheSameId()
    {
        var map = await _storage.GetMapInfo($"surf_cancel_submission_{Guid.NewGuid():N}");
        const long steamId = 76561198000000991;
        await EnsurePlayerAsync(steamId);
        var command = CreateCommand(map.MapName, steamId, Guid.NewGuid());
        using var cancellation = new CancellationTokenSource();
        var finalized = false;
        _storage.Db.Aop.OnLogExecuted = (sql, _) =>
        {
            if (sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("surf_run_submissions", StringComparison.OrdinalIgnoreCase))
            {
                finalized = true;
                cancellation.Cancel();
            }
        };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _storage.RunOperationAsync(() => _storage.SubmitBackendRunAsync(command), cancellation.Token));
        }
        finally { _storage.Db.Aop.OnLogExecuted = null; }

        Assert.True(finalized);
        Assert.Equal(0, await _storage.Db.Queryable<RunEntity>().CountAsync(CancellationToken.None));
        Assert.Equal(0, await _storage.Db.Queryable<RunSegmentEntity>().CountAsync(CancellationToken.None));
        Assert.Equal(0, await _storage.Db.Queryable<PlayerBestRunEntity>().CountAsync(CancellationToken.None));
        Assert.Equal(0, await _storage.Db.Queryable<RunSubmissionEntity>().CountAsync(CancellationToken.None));
        Assert.Equal(0, await _storage.Db.Queryable<ScoreRecalcOutboxEntity>().CountAsync(CancellationToken.None));

        var accepted = await _storage.RunOperationAsync(() => _storage.SubmitBackendRunAsync(command), CancellationToken.None);
        var retry = await _storage.SubmitBackendRunAsync(command);
        Assert.Equal(TimerBackendSubmissionDisposition.Accepted, accepted.Disposition);
        Assert.Equal(TimerBackendSubmissionDisposition.AlreadyApplied, retry.Disposition);
        Assert.Equal(accepted.RunId, retry.RunId);
        Assert.Equal(1, await _storage.Db.Queryable<RunEntity>().CountAsync());
        Assert.Equal(2, await _storage.Db.Queryable<RunSegmentEntity>().CountAsync());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(999, 999)]
    [InlineData(9999, 500)]
    [InlineData(9999, 999)]
    public async Task CompletionTimeOutsideSharedDatabaseRangeIsRejectedBeforeAnySql(int year, int millisecond)
    {
        var timestamp = new DateTime(year, 12, 31, 23, 59, 59, millisecond, DateTimeKind.Utc);
        var command = CreateCommand("surf_invalid_date", 76561198000000001, Guid.NewGuid(), finishedAtUtc: timestamp);
        var statements = 0;
        _storage.Db.Aop.OnLogExecuting = (_, _) => statements++;

        await Assert.ThrowsAsync<TimerBackendSubmissionValidationException>(
            () => _storage.SubmitBackendRunAsync(command));
        Assert.Equal(0, statements);
    }

    [Theory]
    [InlineData(float.MaxValue)]
    [InlineData(-float.MaxValue)]
    public async Task MotionBeyondStorableFloatRangeIsRejectedBeforeAnySql(float velocity)
    {
        // float.MaxValue is finite but exceeds MySQL's FLOAT range; it must be a validation
        // failure (InvalidArgument, quarantined by the sender), not a database error it retries.
        var command = CreateCommand("surf_float_bounds", 76561198000000001, Guid.NewGuid(),
                                    motion: new TimerBackendMotion { VelocityStartX = velocity });
        var statements = 0;
        _storage.Db.Aop.OnLogExecuting = (_, _) => statements++;

        await Assert.ThrowsAsync<TimerBackendSubmissionValidationException>(
            () => _storage.SubmitBackendRunAsync(command));
        Assert.Equal(0, statements);
    }

    [Fact]
    public async Task WipingRecordsRetainsTheReceiptSoAnOldRetryCannotResurrectThem()
    {
        var map = await _storage.GetMapInfo("surf_wiped_submission");
        const long steamId = 76561198000000071;
        await EnsurePlayerAsync(steamId);
        var command = CreateCommand(map.MapName, steamId, Guid.NewGuid());
        var original = await _storage.SubmitBackendRunAsync(command);
        await _storage.RemoveMapRecords(map.MapName);

        var retry = await _storage.SubmitBackendRunAsync(command);
        Assert.Equal(TimerBackendSubmissionDisposition.AlreadyApplied, retry.Disposition);
        Assert.Equal(original.RunId, retry.RunId);
        Assert.False(await _storage.Db.Queryable<RunEntity>().Where(x => x.MapId == map.MapId).AnyAsync());
        Assert.False(await _storage.Db.Queryable<PlayerBestRunEntity>().Where(x => x.MapId == map.MapId).AnyAsync());
    }

    private async Task EnsurePlayerAsync(long steamId)
        => await _storage.GetPlayerProfile(new SteamID(checked((ulong)steamId)), "Submission test player");

    private static TimerBackendRunSubmissionCommand CreateCommand(string mapName,
                                                                    long steamId,
                                                                    Guid submissionId,
                                                                    TimerBackendRunKind kind = TimerBackendRunKind.Main,
                                                                    int stage = 0,
                                                                    int style = 2,
                                                                    int track = 0,
                                                                    long timeMicros = 80_000_000,
                                                                    double styleFactor = 1.25, DateTime? finishedAtUtc = null,
                                                                    TimerBackendMotion? motion = null)
        => new ()
        {
            SubmissionId = submissionId,
            SteamId = steamId,
            MapName = mapName,
            Kind = kind,
            Stage = stage,
            Style = style,
            Track = track,
            TimeMicros = timeMicros,
            Jumps = 7,
            Strafes = 13,
            Sync = 98.5f,
            Motion = motion ?? new TimerBackendMotion { VelocityStartX = 1, VelocityEndZ = 2, VelocityMaxY = 3, VelocityAvgX = 4 },
            Checkpoints =
            [
                new TimerBackendSubmissionCheckpoint
                {
                    CheckpointIndex = 1, TimeMicros = timeMicros / 2, Sync = 97,
                    Motion = new TimerBackendMotion { VelocityStartX = 10, VelocityEndZ = 11 },
                },
                new TimerBackendSubmissionCheckpoint
                {
                    CheckpointIndex = 2, TimeMicros = timeMicros - 1, Sync = 96,
                    Motion = new TimerBackendMotion { VelocityStartY = 12, VelocityMaxZ = 13 },
                },
            ],
            FinishedAtUtc = finishedAtUtc ?? new DateTime(2026, 9, 15, 1, 2, 3, DateTimeKind.Utc),
            RulesetVersion = 1,
            StyleFactor = styleFactor,
        };

    public void Dispose()
    {
        _storage.Shutdown();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // SQLite cleanup is best effort on a locked test runner connection.
        }
    }
}
