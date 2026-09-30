using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Timer.RequestManager.Backend;

/// <summary>
/// Result of a backend-owned score administration operation. The values describe durable
/// Outbox work that was committed; score calculation itself remains asynchronous.
/// </summary>
public sealed class TimerBackendScoreAdministrationResult
{
    /// <summary>
    /// Whether the named map still existed when a single-map operation acquired its map lock.
    /// <see cref="MapsAffected"/> is zero when this is false.
    /// </summary>
    public bool MapFound { get; init; }

    /// <summary>Number of maps whose administrative transaction committed.</summary>
    public int MapsAffected { get; init; }

    /// <summary>Number of distinct score boards for which a new Outbox generation was queued.</summary>
    public int BoardsQueued { get; init; }

    /// <summary>Number of previously dead-lettered boards made pending again.</summary>
    public int DeadLettersRequeued { get; init; }

    /// <summary>
    /// Number of existing boards skipped because the current backend policy does not configure
    /// their style. Configure an explicit zero factor before rerunning the command when the
    /// intended policy is to remove those scores.
    /// </summary>
    public int DisabledStyleBoardsSkipped { get; init; }

    /// <summary>The tier observed under the map lock before a tier operation.</summary>
    public byte? PreviousTier { get; init; }

    /// <summary>The tier committed by a tier operation.</summary>
    public byte? CurrentTier { get; init; }

    /// <summary>
    /// For an all-maps requeue: maps that were skipped because their policy could not be applied
    /// (each entry is "map: reason"). Every other map was still processed.
    /// </summary>
    public IReadOnlyList<string> FailedMaps { get; init; } = [];
}

/// <summary>
/// Backend-only score administration facade. It has no transport registration; callers must
/// already have process-local access to <see cref="TimerBackendStorage"/>.
/// </summary>
public sealed class TimerBackendScoreAdministration
{
    private readonly TimerBackendStorage _storage;

    public TimerBackendScoreAdministration(TimerBackendStorage storage)
        => _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    /// <summary>
    /// Changes a map's main-track tier and durably queues every affected configured main-track
    /// board on that map in the same map-locked transaction.
    /// </summary>
    public Task<TimerBackendScoreAdministrationResult> SetMapTierAsync(
        string mapName,
        byte tier,
        IReadOnlyDictionary<int, double> styleFactors)
        => _storage.SetMapTierAndRequeueScoresAsync(mapName, tier, styleFactors);

    /// <summary>
    /// Applies the current score-policy factors to one existing map. This includes recovery of
    /// compatible dead-lettered Outbox rows without requiring a new personal best.
    /// </summary>
    public Task<TimerBackendScoreAdministrationResult> RequeueScoresAsync(
        string mapName,
        IReadOnlyDictionary<int, double> styleFactors)
        => _storage.RequeueMapScorePolicyAsync(mapName, styleFactors);

    /// <summary>
    /// Applies the current score-policy factors to every map present when this operation starts.
    /// Each map commits independently under its own map lock, avoiding a database-wide lock.
    /// </summary>
    public Task<TimerBackendScoreAdministrationResult> RequeueAllScoresAsync(
        IReadOnlyDictionary<int, double> styleFactors)
        => _storage.RequeueAllScorePoliciesAsync(styleFactors);
}
