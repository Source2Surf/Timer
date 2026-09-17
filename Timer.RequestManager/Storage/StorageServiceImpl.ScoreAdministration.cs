using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models;
using Timer.RequestManager.Backend;

namespace Timer.RequestManager.Storage;

/// <summary>
/// Backend-owned, process-local score administration. These methods deliberately sit outside
/// <see cref="Source2Surf.Timer.Shared.Interfaces.IRequestManager"/>: game servers must not be
/// able to alter policy through the ordinary request-manager surface.
/// </summary>
internal sealed partial class StorageServiceImpl
{
    /// <summary>
    /// Changes a map's main-track tier and queues affected main-track boards governed by the
    /// supplied policy in one map-locked transaction. No map is implicitly created for an
    /// administrative request.
    /// </summary>
    internal async Task<TimerBackendScoreAdministrationResult> SetMapTierAndRequeueScoresAsync(
        string mapName,
        byte tier,
        IReadOnlyDictionary<int, double> styleFactors)
    {
        ValidateAdministrativeMapName(mapName);
        ValidateScorePolicy(styleFactors);

        if (tier == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tier), "Tier must be from 1 through 255.");
        }

        var mapKey = ToMapKey(mapName);
        var map = await FindMapByNameAsync(mapKey);
        if (map is null)
        {
            return new TimerBackendScoreAdministrationResult { MapFound = false };
        }

        // Seed gates acquire their own map transaction. They must complete before this operation
        // takes a map lock, otherwise two local callers can invert the seed-gate/map-lock order.
        // The low-frequency administrative path seeds the complete main-map projection so its
        // player-total preflight uses exactly the same ranked rows as the score worker.
        await EnsureBestRunsSeededForMapAsync(map.MapId, RunType.Main);

        byte? previousTier = null;
        var boardsQueued = 0;
        var deadLettersRequeued = 0;
        var disabledStyleBoardsSkipped = 0;

        await WithRecordTransactionAsync(async () =>
        {
            // Reset values on a transaction-conflict retry so only the committed attempt is
            // reported to the CLI caller.
            previousTier = null;
            boardsQueued = 0;
            deadLettersRequeued = 0;
            disabledStyleBoardsSkipped = 0;

            await LockMapAsync(map.MapId);
            var lockedMap = await _db.Queryable<MapEntity>()
                                     .Where(x => x.MapId == map.MapId)
                                     .FirstAsync(OperationCancellation)
                            ?? throw new InvalidOperationException($"Map {map.MapId} disappeared while acquiring its lock.");

            // A stale cache or a delete/reinsert race must never update an unrelated map which
            // happened to reuse an identity on a provider that permits it.
            if (!string.Equals(lockedMap.File, mapKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Map '{mapName}' changed while its administrative operation was starting.");
            }

            var boards = await GetKnownScoreBoardsInCurrentRecordTransactionAsync(map.MapId);
            var affectedBoards = boards.Where(x => x.Track == 0).ToList();
            var trackPools = ValidateMainTrackTierCanBeRepresented(
                lockedMap, tier, affectedBoards, styleFactors);
            await ValidateMainTrackPlayerTotalsCanBeRepresentedAsync(
                map.MapId, tier, affectedBoards, trackPools, styleFactors);

            previousTier = lockedMap.Tier;
            if (lockedMap.Tier != tier)
            {
                await _db.Updateable<MapEntity>()
                         .SetColumns(x => x.Tier == tier)
                         .Where(x => x.MapId == map.MapId && x.Tier != tier)
                         .ExecuteCommandAsync(OperationCancellation);
            }

            var queued = await RequeueKnownScoreBoardsInCurrentRecordTransactionAsync(
                map.MapId, affectedBoards, styleFactors, DateTime.UtcNow);
            boardsQueued = queued.BoardsQueued;
            deadLettersRequeued = queued.DeadLettersRequeued;
            disabledStyleBoardsSkipped = queued.DisabledStyleBoardsSkipped;
        });

        WakeScoreRecalcWorker();
        return new TimerBackendScoreAdministrationResult
        {
            MapFound = true,
            MapsAffected = 1,
            BoardsQueued = boardsQueued,
            DeadLettersRequeued = deadLettersRequeued,
            DisabledStyleBoardsSkipped = disabledStyleBoardsSkipped,
            PreviousTier = previousTier,
            CurrentTier = tier,
        };
    }

    /// <summary>
    /// Queues a policy refresh for one existing map. A dead-lettered row is reactivated by the
    /// usual generation merge, so retry metadata is reset only as part of a durable new request.
    /// </summary>
    internal async Task<TimerBackendScoreAdministrationResult> RequeueMapScorePolicyAsync(
        string mapName,
        IReadOnlyDictionary<int, double> styleFactors)
    {
        ValidateAdministrativeMapName(mapName);
        ValidateScorePolicy(styleFactors);

        var mapKey = ToMapKey(mapName);
        var map = await FindMapByNameAsync(mapKey);
        if (map is null)
        {
            return new TimerBackendScoreAdministrationResult { MapFound = false };
        }

        var boardsQueued = 0;
        var deadLettersRequeued = 0;
        var disabledStyleBoardsSkipped = 0;

        await WithRecordTransactionAsync(async () =>
        {
            boardsQueued = 0;
            deadLettersRequeued = 0;
            disabledStyleBoardsSkipped = 0;

            await LockMapAsync(map.MapId);
            var lockedMap = await _db.Queryable<MapEntity>()
                                     .Where(x => x.MapId == map.MapId)
                                     .FirstAsync(OperationCancellation)
                            ?? throw new InvalidOperationException($"Map {map.MapId} disappeared while acquiring its lock.");
            if (!string.Equals(lockedMap.File, mapKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Map '{mapName}' changed while its administrative operation was starting.");
            }

            var boards = await GetKnownScoreBoardsInCurrentRecordTransactionAsync(map.MapId);
            var queued = await RequeueKnownScoreBoardsInCurrentRecordTransactionAsync(
                map.MapId, boards, styleFactors, DateTime.UtcNow);
            boardsQueued = queued.BoardsQueued;
            deadLettersRequeued = queued.DeadLettersRequeued;
            disabledStyleBoardsSkipped = queued.DisabledStyleBoardsSkipped;
        });

        WakeScoreRecalcWorker();
        return new TimerBackendScoreAdministrationResult
        {
            MapFound = true,
            MapsAffected = 1,
            BoardsQueued = boardsQueued,
            DeadLettersRequeued = deadLettersRequeued,
            DisabledStyleBoardsSkipped = disabledStyleBoardsSkipped,
        };
    }

    /// <summary>
    /// Queues a policy refresh map by map. A single map transaction is intentionally the unit of
    /// atomicity: taking every map lock at once would turn a long recovery into a database-wide
    /// writer outage.
    /// </summary>
    internal async Task<TimerBackendScoreAdministrationResult> RequeueAllScorePoliciesAsync(
        IReadOnlyDictionary<int, double> styleFactors)
    {
        ValidateScorePolicy(styleFactors);

        var mapNames = await _db.Queryable<MapEntity>()
                                .OrderBy(x => x.File)
                                .Select(x => x.File)
                                .ToListAsync(OperationCancellation);
        var mapsAffected = 0;
        var boardsQueued = 0;
        var deadLettersRequeued = 0;
        var disabledStyleBoardsSkipped = 0;

        foreach (var mapName in mapNames)
        {
            var result = await RequeueMapScorePolicyAsync(mapName, styleFactors);
            if (!result.MapFound)
            {
                // A concurrent map deletion is harmless. Its remaining dead-lettered row has no
                // score board that can be safely recalculated, so leave it visible for operators.
                continue;
            }

            mapsAffected += result.MapsAffected;
            boardsQueued += result.BoardsQueued;
            deadLettersRequeued += result.DeadLettersRequeued;
            disabledStyleBoardsSkipped += result.DisabledStyleBoardsSkipped;
        }

        return new TimerBackendScoreAdministrationResult
        {
            MapFound = true,
            MapsAffected = mapsAffected,
            BoardsQueued = boardsQueued,
            DeadLettersRequeued = deadLettersRequeued,
            DisabledStyleBoardsSkipped = disabledStyleBoardsSkipped,
        };
    }

    private async Task<List<AdministrativeScoreBoard>> GetKnownScoreBoardsInCurrentRecordTransactionAsync(ulong mapId)
    {
        var runBoards = await _db.Queryable<RunEntity>()
                                 .Where(x => x.MapId == mapId
                                             && x.RunType == RunType.Main
                                             && x.Stage == 0)
                                 .GroupBy(x => new { x.Style, x.Track })
                                 .Select(x => new AdministrativeScoreBoardRow
                                 {
                                     Style = x.Style,
                                     Track = x.Track,
                                 })
                                 .ToListAsync(OperationCancellation);
        var scoreBoards = await _db.Queryable<PlayerTrackScoreEntity>()
                                   .Where(x => x.MapId == mapId)
                                   .GroupBy(x => new { x.Style, x.Track })
                                   .Select(x => new AdministrativeScoreBoardRow
                                   {
                                       Style = x.Style,
                                       Track = x.Track,
                                   })
                                   .ToListAsync(OperationCancellation);
        var outboxBoards = await _db.Queryable<ScoreRecalcOutboxEntity>()
                                    .Where(x => x.MapId == mapId)
                                    .Select(x => new AdministrativeOutboxBoardRow
                                    {
                                        Style = x.Style,
                                        Track = x.Track,
                                        IsDeadLettered = x.DeadLetteredAtUtc != null,
                                    })
                                    .ToListAsync(OperationCancellation);

        var boards = new Dictionary<(int Style, ushort Track), AdministrativeScoreBoard>();
        foreach (var board in runBoards)
        {
            boards[(board.Style, board.Track)] = new AdministrativeScoreBoard(
                board.Style, board.Track, HasMainRun: true, IsDeadLettered: false);
        }

        foreach (var board in scoreBoards)
        {
            var key = (board.Style, board.Track);
            if (boards.TryGetValue(key, out var existing))
            {
                boards[key] = existing;
            }
            else
            {
                boards[key] = new AdministrativeScoreBoard(
                    board.Style, board.Track, HasMainRun: false, IsDeadLettered: false);
            }
        }

        foreach (var board in outboxBoards)
        {
            var key = (board.Style, board.Track);
            if (boards.TryGetValue(key, out var existing))
            {
                boards[key] = existing with { IsDeadLettered = board.IsDeadLettered };
            }
            else
            {
                boards[key] = new AdministrativeScoreBoard(
                    board.Style, board.Track, HasMainRun: false, IsDeadLettered: board.IsDeadLettered);
            }
        }

        return boards.Values
                     .OrderBy(x => x.Style)
                     .ThenBy(x => x.Track)
                     .ToList();
    }

    private async Task<AdministrativeQueueResult> RequeueKnownScoreBoardsInCurrentRecordTransactionAsync(
        ulong mapId,
        IReadOnlyList<AdministrativeScoreBoard> boards,
        IReadOnlyDictionary<int, double> styleFactors,
        DateTime nowUtc)
    {
        var boardsQueued = 0;
        var deadLettersRequeued = 0;
        var disabledStyleBoardsSkipped = 0;

        foreach (var board in boards)
        {
            if (!styleFactors.TryGetValue(board.Style, out var styleFactor))
            {
                disabledStyleBoardsSkipped++;
                continue;
            }

            await EnqueueScoreRecalcInCurrentRecordTransactionAsync(
                mapId, board.Style, board.Track, styleFactor, nowUtc);
            boardsQueued++;
            if (board.IsDeadLettered)
            {
                deadLettersRequeued++;
            }
        }

        return new AdministrativeQueueResult(boardsQueued, deadLettersRequeued, disabledStyleBoardsSkipped);
    }

    private static IReadOnlyDictionary<int, double> ValidateMainTrackTierCanBeRepresented(
        MapEntity map,
        byte tier,
        IReadOnlyList<AdministrativeScoreBoard> boards,
        IReadOnlyDictionary<int, double> styleFactors)
    {
        // A tier edit affects only track 0. Validate every configured active main-style board,
        // and validate style 0 even on a fresh map so an impossible policy cannot be stored before
        // the first completion arrives.
        var styles = new HashSet<int> { 0 };
        foreach (var board in boards)
        {
            if (board.HasMainRun && board.Track == 0 && styleFactors.ContainsKey(board.Style))
            {
                styles.Add(board.Style);
            }
        }

        var trackPools = new Dictionary<int, double>(styles.Count);
        foreach (var style in styles)
        {
            var styleFactor = styleFactors[style];
            if (styleFactor == 0)
            {
                continue;
            }

            var trackPool = ScoreCalculator.CalculateTrackPool(tier,
                                                                isBonus: false,
                                                                basePot: map.BasePot,
                                                                styleFactor: styleFactor);
            _ = CalculateRepresentablePlayerTrackScore(
                trackPool,
                rank: 1,
                total: 1,
                $"Tier {tier} with score factor {styleFactor} for style {style} would produce an unrepresentable main-track score.");
            trackPools.Add(style, trackPool);
        }

        return trackPools;
    }

    /// <summary>
    /// A tier edit may change several style boards for the same player. Before committing the
    /// edit, calculate the exact replacement score for every current rank and ensure the resulting
    /// player total still fits the persisted unsigned integer. The main best-run projection is
    /// seeded before the caller acquires its map lock, so this uses the same ranking source as the
    /// score worker without adding work to the normal run-submission path.
    /// </summary>
    private async Task ValidateMainTrackPlayerTotalsCanBeRepresentedAsync(
        ulong mapId,
        byte tier,
        IReadOnlyList<AdministrativeScoreBoard> affectedBoards,
        IReadOnlyDictionary<int, double> trackPools,
        IReadOnlyDictionary<int, double> styleFactors)
    {
        var affectedStyles = affectedBoards.Where(x => styleFactors.ContainsKey(x.Style))
                                           .Select(x => x.Style)
                                           .Distinct()
                                           .ToList();
        if (affectedStyles.Count == 0)
        {
            return;
        }

        var projectedScores = new List<AdministrativePlayerScoreRow>();
        foreach (var style in affectedStyles)
        {
            if (!trackPools.TryGetValue(style, out var trackPool))
            {
                // A zero factor removes the board's existing scores and creates no new rows.
                continue;
            }

            var rankedPlayers = await GetRankedPlayersAsync(mapId, style, track: 0);
            for (var index = 0; index < rankedPlayers.Count; index++)
            {
                projectedScores.Add(new AdministrativePlayerScoreRow
                {
                    SteamId = rankedPlayers[index].SteamId,
                    Points = CalculateRepresentablePlayerTrackScore(
                        trackPool,
                        index + 1,
                        rankedPlayers.Count,
                        $"Tier {tier} would produce an unrepresentable score for style {style}."),
                });
            }
        }

        var affectedExistingScores = await _db.Queryable<PlayerTrackScoreEntity>()
                                               .Where(x => x.MapId == mapId
                                                           && x.Track == 0
                                                           && affectedStyles.Contains(x.Style))
                                               .Select(x => new AdministrativeTargetPlayerScoreRow
                                               {
                                                   SteamId = x.SteamId,
                                                   Points = x.Points,
                                               })
                                               .ToListAsync(OperationCancellation);

        var affectedPlayers = affectedExistingScores.Select(x => x.SteamId)
                                                    .Concat(projectedScores.Select(x => x.SteamId))
                                                    .Distinct()
                                                    .ToList();
        if (affectedPlayers.Count == 0)
        {
            return;
        }

        // Hold every affected player in the standard ascending-id order before reading score
        // totals. A concurrent recalculation for another map obeys the same order, so the
        // preflight sees its committed state instead of racing a cross-map total update.
        await LockPlayersForPointsAsync(affectedPlayers);

        var currentScores = new List<AdministrativePlayerScoreRow>();
        foreach (var batch in affectedPlayers.Chunk(500))
        {
            var rows = await _db.Queryable<PlayerTrackScoreEntity>()
                                .Where(x => batch.Contains(x.SteamId))
                                .Select(x => new AdministrativePlayerScoreRow
                                {
                                    SteamId = x.SteamId,
                                    Points = x.Points,
                                })
                                .ToListAsync(OperationCancellation);
            currentScores.AddRange(rows);
        }

        var projectedTotals = new Dictionary<long, ulong>(affectedPlayers.Count);
        foreach (var score in currentScores)
        {
            var current = projectedTotals.GetValueOrDefault(score.SteamId);
            projectedTotals[score.SteamId] = AddAdministrativePointsChecked(
                current, score.Points, "Existing score rows exceed the administration preflight range.");
        }

        // Existing targeted rows are replaced by this recalculation (or removed for a zero
        // factor). Remove them from the authoritative score-row total before adding the upper
        // exact new score for each ranked player below.
        foreach (var score in affectedExistingScores)
        {
            var current = projectedTotals.GetValueOrDefault(score.SteamId);
            if (current < score.Points)
            {
                throw new InvalidOperationException(
                    "Score administration preflight found an inconsistent player-track-score projection.");
            }

            projectedTotals[score.SteamId] = current - score.Points;
        }

        foreach (var score in projectedScores)
        {
            var current = projectedTotals.GetValueOrDefault(score.SteamId);
            projectedTotals[score.SteamId] = AddAdministrativePointsChecked(
                current,
                score.Points,
                "Score administration preflight exceeded its arithmetic range.");
        }

        foreach (var (steamId, projectedTotal) in projectedTotals)
        {
            if (projectedTotal > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Tier {tier} could make player {steamId}'s total score exceed {uint.MaxValue}.");
            }
        }
    }

    private static uint CalculateRepresentablePlayerTrackScore(double trackPool,
                                                                int rank,
                                                                int total,
                                                                string failureMessage)
    {
        var roundedPoints = Math.Round(
            ScoreCalculator.CalculatePlayerTrackScore(trackPool, rank, total),
            MidpointRounding.ToEven);
        if (!double.IsFinite(roundedPoints) || roundedPoints < 0 || roundedPoints > uint.MaxValue)
        {
            throw new InvalidOperationException(failureMessage);
        }

        return (uint)roundedPoints;
    }

    private static ulong AddAdministrativePointsChecked(ulong current, ulong points, string overflowMessage)
    {
        try
        {
            return checked(current + points);
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException(overflowMessage, exception);
        }
    }
    private static void ValidateScorePolicy(IReadOnlyDictionary<int, double> styleFactors)
    {
        ArgumentNullException.ThrowIfNull(styleFactors);

        if (!styleFactors.ContainsKey(0))
        {
            throw new ArgumentException(
                "Administrative score recalculation requires an explicit style 0 factor.",
                nameof(styleFactors));
        }

        foreach (var (style, factor) in styleFactors)
        {
            if ((uint)style >= TimerConstants.MAX_STYLE)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(styleFactors), $"Style {style} is outside the supported range.");
            }

            if (!double.IsFinite(factor) || factor is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(styleFactors), $"Style factor for style {style} must be finite and from 0 through 100.");
            }
        }
    }

    private static void ValidateAdministrativeMapName(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            throw new ArgumentException("A map name is required.", nameof(mapName));
        }
    }

    private sealed class AdministrativeScoreBoardRow
    {
        public int Style { get; set; }
        public ushort Track { get; set; }
    }

    private sealed class AdministrativeOutboxBoardRow
    {
        public int Style { get; set; }
        public ushort Track { get; set; }
        public bool IsDeadLettered { get; set; }
    }

    private sealed class AdministrativePlayerScoreRow
    {
        public long SteamId { get; set; }
        public uint Points { get; set; }
    }

    private sealed class AdministrativeTargetPlayerScoreRow
    {
        public long SteamId { get; set; }
        public uint Points { get; set; }
    }

    private sealed record AdministrativeScoreBoard(int Style,
                                                   ushort Track,
                                                   bool HasMainRun,
                                                   bool IsDeadLettered);

    private readonly record struct AdministrativeQueueResult(int BoardsQueued,
                                                               int DeadLettersRequeued,
                                                               int DisabledStyleBoardsSkipped);
}
