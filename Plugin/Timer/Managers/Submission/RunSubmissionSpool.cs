/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
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
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Backend.Rpc.Contracts;

namespace Source2Surf.Timer.Managers.Submission;

/// <summary>
/// Process-local queue for v1 run submissions. It deliberately has no disk or transport
/// dependency: entries survive a backend outage while this plugin process is alive, but are
/// intentionally lost when the process exits or this queue is shut down.
/// </summary>
internal sealed class RunSubmissionSpool : IManager, IDisposable
{
    private const int DefaultCapacity        = 1_024;
    private const int DefaultMaximumAttempts = 8;
    private const int MaximumBatchSize       = 128;
    private const int MaximumErrorLength     = 2_048;
    private const string LegacyDatabaseFileName = "run-submissions.db";

    private static readonly TimeSpan LeaseDuration       = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumRetryBackoff = TimeSpan.FromMinutes(5);

    private readonly object                                     _gate = new ();
    private readonly Dictionary<Guid, RunSubmissionQueueEntry> _entries = [];
    private readonly int                                        _capacity;
    private readonly string?                                    _legacyDatabasePath;
    private readonly Func<string, bool>                         _legacyDatabaseExists;
    private bool                                                _initialized;
    private long                                                _claimSequence;

    public RunSubmissionSpool(InterfaceBridge bridge, ILogger<RunSubmissionSpool> logger)
        : this(logger, legacyDatabasePath: GetLegacyDatabasePath(bridge))
    {
        // The path is captured only for a later remote-enabled compatibility check. The queue
        // itself never opens, writes, moves, or deletes this legacy database.
    }

    internal RunSubmissionSpool(ILogger<RunSubmissionSpool> logger,
                                int                         capacity = DefaultCapacity,
                                int                         maximumAttempts = DefaultMaximumAttempts,
                                string?                     legacyDatabasePath = null,
                                Func<string, bool>?         legacyDatabaseExists = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (maximumAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        // Retained only for source compatibility with the former persistent spool test seam.
        // Transient delivery failures are always retried without an attempt limit in memory.
        _ = maximumAttempts;
        _capacity             = capacity;
        _legacyDatabasePath   = legacyDatabasePath;
        _legacyDatabaseExists = legacyDatabaseExists ?? File.Exists;
    }

    public bool Init()
    {
        lock (_gate)
        {
            _initialized = true;
            return true;
        }
    }

    public void Shutdown()
    {
        lock (_gate)
        {
            // This is intentionally the terminal boundary for volatile work. There is no
            // persistence or recovery path after shutdown/process restart.
            _entries.Clear();
            _initialized = false;
        }
    }

    public void Dispose()
    {
        Shutdown();
    }

    /// <summary>
    /// Enqueues an entire v1 wire request. An empty id is replaced in the in-memory copy and
    /// returned to the caller; a non-empty id remains the in-process idempotency key.
    /// </summary>
    internal SubmissionSpoolEnqueueResult Enqueue(SubmitRunRequest request, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var submissionId = request.SubmissionId == Guid.Empty ? Guid.NewGuid() : request.SubmissionId;
        var now          = NormalizeUtc(nowUtc ?? DateTime.UtcNow);
        var queued       = CloneRequest(request, submissionId);

        lock (_gate)
        {
            EnsureInitialized();

            if (_entries.TryGetValue(submissionId, out var existing))
            {
                return new SubmissionSpoolEnqueueResult(submissionId,
                                                        RequestsEqual(existing.Request, queued)
                                                            ? SubmissionSpoolEnqueueDisposition.AlreadyQueued
                                                            : SubmissionSpoolEnqueueDisposition.ConflictingSubmissionId);
            }

            // Quarantined entries count toward the cap so dead letters stay bounded, but they
            // were already logged when quarantined and can never be sent. Evict the oldest one
            // rather than refusing every new run once enough of them have accumulated.
            if (_entries.Count >= _capacity && !TryEvictOldestQuarantined())
            {
                return new SubmissionSpoolEnqueueResult(submissionId, SubmissionSpoolEnqueueDisposition.CapacityExceeded);
            }

            _entries.Add(submissionId, new RunSubmissionQueueEntry
            {
                SubmissionId     = submissionId,
                Request          = queued,
                NextAttemptAtUtc = now,
                CreatedAtUtc     = now,
                UpdatedAtUtc     = now,
            });

            return new SubmissionSpoolEnqueueResult(submissionId, SubmissionSpoolEnqueueDisposition.Enqueued);
        }
    }

    private bool TryEvictOldestQuarantined()
    {
        RunSubmissionQueueEntry? oldest = null;
        foreach (var entry in _entries.Values)
        {
            if (entry.QuarantinedAtUtc is { } quarantinedAt
                && (oldest is null || quarantinedAt < oldest.QuarantinedAtUtc!.Value))
            {
                oldest = entry;
            }
        }

        return oldest is not null && _entries.Remove(oldest.SubmissionId);
    }

    /// <summary>
    /// Claims at most <paramref name="maximumCount"/> due entries for one sender. A fresh
    /// opaque token is issued for each batch. All non-quarantined entries remain eligible for
    /// unbounded retry while this process is alive.
    /// </summary>
    /// <param name="finalAttemptForClaimsUpTo">
    /// During a shutdown drain, also claims entries still waiting out a retry backoff whose
    /// last claim is at or before this <see cref="CurrentClaimSequence"/> value, so each gets
    /// one last attempt before the volatile queue is discarded. An entry that fails again was
    /// claimed after that point and is not re-claimed by this rule. A sequence rather than a
    /// timestamp keeps the ordering exact even when both fall within one clock tick.
    /// </param>
    internal IReadOnlyList<RunSubmissionSpoolLease> ClaimDueBatch(int       maximumCount,
                                                                    string    leaseOwner,
                                                                    DateTime? nowUtc = null,
                                                                    bool      retryIndefinitely = false,
                                                                    long?     finalAttemptForClaimsUpTo = null)
    {
        if (maximumCount is <= 0 or > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);

        // The old durable spool exposed this flag. It is now implicit for every transient
        // failure, but preserving the parameter avoids changing sender call sites.
        _ = retryIndefinitely;

        var now = NormalizeUtc(nowUtc ?? DateTime.UtcNow);

        lock (_gate)
        {
            EnsureInitialized();

            var leaseToken = string.Concat(leaseOwner, ":", Guid.NewGuid().ToString("N"));
            var candidates = _entries.Values
                                     .Where(entry => entry.QuarantinedAtUtc is null
                                                     && (IsDue(entry, now) || NeedsFinalAttempt(entry, now, finalAttemptForClaimsUpTo)))
                                     .OrderBy(entry => entry.NextAttemptAtUtc)
                                     .ThenBy(entry => entry.CreatedAtUtc)
                                     .Take(maximumCount)
                                     .ToList();
            var claimed = new List<RunSubmissionSpoolLease>(candidates.Count);
            var claimSequence = candidates.Count == 0 ? _claimSequence : ++_claimSequence;

            foreach (var entry in candidates)
            {
                entry.AttemptCount++;
                entry.LeaseToken       = leaseToken;
                entry.LeaseUntilUtc    = now + LeaseDuration;
                entry.LastClaimSequence = claimSequence;
                entry.UpdatedAtUtc     = now;

                // Do not hand a mutable internal request to a transport. In particular, a
                // caller mutating its request after enqueue must not change the retry payload.
                claimed.Add(new RunSubmissionSpoolLease(entry.SubmissionId,
                                                          leaseToken,
                                                          CloneRequest(entry.Request, entry.SubmissionId),
                                                          entry.AttemptCount));
            }

            return claimed;
        }
    }

    /// <summary>Confirms successful delivery. A stale sender cannot remove a later lease.</summary>
    internal bool Acknowledge(Guid submissionId, string leaseToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        lock (_gate)
        {
            EnsureInitialized();

            return _entries.TryGetValue(submissionId, out var entry)
                   && string.Equals(entry.LeaseToken, leaseToken, StringComparison.Ordinal)
                   && _entries.Remove(submissionId);
        }
    }

    /// <summary>
    /// Legacy failure operation. It has the same unbounded transient-retry behavior as
    /// <see cref="Retry"/>; only an explicit <see cref="Quarantine"/> is terminal.
    /// </summary>
    internal bool Fail(Guid submissionId, string leaseToken, string error, DateTime? nowUtc = null)
    {
        return Retry(submissionId, leaseToken, error, nowUtc);
    }

    /// <summary>
    /// Records a transient transport outcome and schedules an unbounded retry with capped
    /// exponential backoff. This never quarantines an entry merely because an outage is long.
    /// </summary>
    internal bool Retry(Guid submissionId, string leaseToken, string error, DateTime? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        var now = NormalizeUtc(nowUtc ?? DateTime.UtcNow);

        lock (_gate)
        {
            EnsureInitialized();

            if (!_entries.TryGetValue(submissionId, out var entry)
                || entry.QuarantinedAtUtc is not null
                || !string.Equals(entry.LeaseToken, leaseToken, StringComparison.Ordinal))
            {
                return false;
            }

            entry.LastError        = TruncateError(error);
            entry.LeaseToken       = null;
            entry.LeaseUntilUtc    = null;
            entry.NextAttemptAtUtc = now + GetRetryBackoffWithJitter(entry.AttemptCount, entry.SubmissionId);
            entry.UpdatedAtUtc     = now;
            return true;
        }
    }

    /// <summary>
    /// Immediately isolates a leased submission in memory. This is reserved for errors that
    /// cannot be repaired by replaying the same idempotency key, such as authorization,
    /// validation, or payload-conflict failures.
    /// </summary>
    internal bool Quarantine(Guid submissionId, string leaseToken, string error, DateTime? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);

        var now = NormalizeUtc(nowUtc ?? DateTime.UtcNow);

        lock (_gate)
        {
            EnsureInitialized();

            if (!_entries.TryGetValue(submissionId, out var entry)
                || entry.QuarantinedAtUtc is not null
                || !string.Equals(entry.LeaseToken, leaseToken, StringComparison.Ordinal))
            {
                return false;
            }

            entry.LastError        = TruncateError(error);
            entry.QuarantinedAtUtc = now;
            entry.LeaseToken       = null;
            entry.LeaseUntilUtc    = null;
            entry.UpdatedAtUtc     = now;
            return true;
        }
    }

    /// <summary>
    /// Releases a lease without making a delivery decision. A later same-process attempt can
    /// replay the unchanged submission id. Shutdown discards it afterward.
    /// </summary>
    internal bool ReleaseLease(Guid submissionId, string leaseToken, DateTime? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        var now = NormalizeUtc(nowUtc ?? DateTime.UtcNow);

        lock (_gate)
        {
            EnsureInitialized();

            if (!_entries.TryGetValue(submissionId, out var entry)
                || entry.QuarantinedAtUtc is not null
                || !string.Equals(entry.LeaseToken, leaseToken, StringComparison.Ordinal))
            {
                return false;
            }

            entry.LeaseToken       = null;
            entry.LeaseUntilUtc    = null;
            entry.NextAttemptAtUtc = now;
            entry.UpdatedAtUtc     = now;
            return true;
        }
    }

    internal SubmissionSpoolSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            EnsureInitialized();

            var quarantined = _entries.Values.Count(entry => entry.QuarantinedAtUtc is not null);
            return new SubmissionSpoolSnapshot(_entries.Count, _entries.Count - quarantined, quarantined);
        }
    }

    /// <summary>
    /// Reports a legacy persistent queue that could contain unacknowledged work. This is a
    /// read-only existence check for sender startup compatibility; no LiteDB code is retained.
    /// </summary>
    internal bool TryGetLegacyDatabasePath(out string legacyDatabasePath)
    {
        if (!string.IsNullOrEmpty(_legacyDatabasePath) && _legacyDatabaseExists(_legacyDatabasePath))
        {
            legacyDatabasePath = _legacyDatabasePath;
            return true;
        }

        legacyDatabasePath = string.Empty;
        return false;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The run submission queue is not initialized.");
        }
    }

    private static string GetLegacyDatabasePath(InterfaceBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        return Path.Combine(bridge.TimerDataPath, LegacyDatabaseFileName);
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc         => value,
            DateTimeKind.Local       => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _                        => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    private static bool IsDue(RunSubmissionQueueEntry entry, DateTime now)
    {
        return entry.NextAttemptAtUtc <= now
               && (entry.LeaseUntilUtc is null || entry.LeaseUntilUtc.Value <= now);
    }

    private static bool NeedsFinalAttempt(RunSubmissionQueueEntry entry, DateTime now, long? finalAttemptForClaimsUpTo)
    {
        return finalAttemptForClaimsUpTo is { } upTo
               && (entry.LeaseUntilUtc is null || entry.LeaseUntilUtc.Value <= now)
               && entry.LastClaimSequence <= upTo;
    }

    /// <summary>The sequence number of the most recent claim; zero before any claim.</summary>
    internal long CurrentClaimSequence
    {
        get
        {
            lock (_gate)
            {
                return _claimSequence;
            }
        }
    }

    private static string TruncateError(string error)
    {
        return error.Length <= MaximumErrorLength ? error : error[..MaximumErrorLength];
    }

    private static TimeSpan GetRetryBackoff(int attemptCount)
    {
        // Attempts are one based. The cap prevents a broken endpoint from becoming a hot loop
        // while retaining unbounded opportunities to retry during a long outage.
        var exponent = Math.Min(Math.Max(attemptCount - 1, 0), 9);
        var seconds  = Math.Min(1 << exponent, (int)MaximumRetryBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan GetRetryBackoffWithJitter(int attemptCount, Guid submissionId)
    {
        var backoff = GetRetryBackoff(attemptCount);

        // Stable jitter prevents several entries from repeatedly reconnecting in lock-step, yet
        // does not rely on process-local randomness for a submission's retry cadence.
        Span<byte> bytes = stackalloc byte[16];
        submissionId.TryWriteBytes(bytes);
        // Keep the shifts unsigned: a high GUID bit must not overflow a checked int-to-uint cast.
        var seed = (uint)bytes[0]
                   | ((uint)bytes[1] << 8)
                   | ((uint)bytes[2] << 16)
                   | ((uint)bytes[3] << 24);
        var maximumJitterMilliseconds = Math.Max(1, (int)Math.Min(backoff.TotalMilliseconds / 5d, 1_000d));
        return backoff + TimeSpan.FromMilliseconds(seed % (uint)(maximumJitterMilliseconds + 1));
    }

    private static SubmitRunRequest CloneRequest(SubmitRunRequest source, Guid submissionId)
    {
        return new SubmitRunRequest
        {
            SubmissionId                  = submissionId,
            SteamId                       = source.SteamId,
            MapName                       = source.MapName,
            RunKind                       = source.RunKind,
            Style                         = source.Style,
            Track                         = source.Track,
            Stage                         = source.Stage,
            TimeMicros                    = source.TimeMicros,
            Jumps                         = source.Jumps,
            Strafes                       = source.Strafes,
            Sync                          = source.Sync,
            Motion                        = CloneMotion(source.Motion),
            Checkpoints                   = CloneCheckpoints(source.Checkpoints),
            FinishedAtUnixTimeMilliseconds = source.FinishedAtUnixTimeMilliseconds,
            ContractVersion                = source.ContractVersion,
            RulesetVersion                 = source.RulesetVersion,
        };
    }

    private static MotionDto CloneMotion(MotionDto? source)
    {
        if (source is null)
        {
            return null!;
        }

        return new MotionDto
        {
            StartX   = source.StartX,
            StartY   = source.StartY,
            StartZ   = source.StartZ,
            AverageX = source.AverageX,
            AverageY = source.AverageY,
            AverageZ = source.AverageZ,
            MaxX     = source.MaxX,
            MaxY     = source.MaxY,
            MaxZ     = source.MaxZ,
            EndX     = source.EndX,
            EndY     = source.EndY,
            EndZ     = source.EndZ,
        };
    }

    private static CheckpointDto[] CloneCheckpoints(CheckpointDto[]? source)
    {
        if (source is null)
        {
            return null!;
        }

        var clone = new CheckpointDto[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            clone[index] = CloneCheckpoint(source[index]);
        }

        return clone;
    }

    private static CheckpointDto CloneCheckpoint(CheckpointDto? source)
    {
        if (source is null)
        {
            return null!;
        }

        return new CheckpointDto
        {
            Index      = source.Index,
            TimeMicros = source.TimeMicros,
            Sync       = source.Sync,
            Motion     = CloneMotion(source.Motion),
        };
    }

    private static bool RequestsEqual(SubmitRunRequest first, SubmitRunRequest second)
    {
        return first.SubmissionId == second.SubmissionId
               && first.SteamId == second.SteamId
               && string.Equals(first.MapName, second.MapName, StringComparison.Ordinal)
               && first.RunKind == second.RunKind
               && first.Style == second.Style
               && first.Track == second.Track
               && first.Stage == second.Stage
               && first.TimeMicros == second.TimeMicros
               && first.Jumps == second.Jumps
               && first.Strafes == second.Strafes
               && FloatBitsEqual(first.Sync, second.Sync)
               && MotionsEqual(first.Motion, second.Motion)
               && CheckpointsEqual(first.Checkpoints, second.Checkpoints)
               && first.FinishedAtUnixTimeMilliseconds == second.FinishedAtUnixTimeMilliseconds
               && first.ContractVersion == second.ContractVersion
               && first.RulesetVersion == second.RulesetVersion;
    }

    private static bool MotionsEqual(MotionDto? first, MotionDto? second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        return first is not null
               && second is not null
               && FloatBitsEqual(first.StartX, second.StartX)
               && FloatBitsEqual(first.StartY, second.StartY)
               && FloatBitsEqual(first.StartZ, second.StartZ)
               && FloatBitsEqual(first.AverageX, second.AverageX)
               && FloatBitsEqual(first.AverageY, second.AverageY)
               && FloatBitsEqual(first.AverageZ, second.AverageZ)
               && FloatBitsEqual(first.MaxX, second.MaxX)
               && FloatBitsEqual(first.MaxY, second.MaxY)
               && FloatBitsEqual(first.MaxZ, second.MaxZ)
               && FloatBitsEqual(first.EndX, second.EndX)
               && FloatBitsEqual(first.EndY, second.EndY)
               && FloatBitsEqual(first.EndZ, second.EndZ);
    }

    private static bool CheckpointsEqual(CheckpointDto[]? first, CheckpointDto[]? second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first is null || second is null || first.Length != second.Length)
        {
            return false;
        }

        for (var index = 0; index < first.Length; index++)
        {
            var left  = first[index];
            var right = second[index];
            if (left is null
                || right is null
                || left.Index != right.Index
                || left.TimeMicros != right.TimeMicros
                || !FloatBitsEqual(left.Sync, right.Sync)
                || !MotionsEqual(left.Motion, right.Motion))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FloatBitsEqual(float first, float second)
    {
        return BitConverter.SingleToInt32Bits(first) == BitConverter.SingleToInt32Bits(second);
    }

    private sealed class RunSubmissionQueueEntry
    {
        public required Guid             SubmissionId     { get; init; }
        public required SubmitRunRequest Request          { get; init; }
        public int                       AttemptCount     { get; set; }
        public required DateTime         NextAttemptAtUtc { get; set; }
        public required DateTime         CreatedAtUtc     { get; init; }
        public required DateTime         UpdatedAtUtc     { get; set; }
        public string?                   LeaseToken       { get; set; }
        public DateTime?                 LeaseUntilUtc    { get; set; }
        public DateTime?                 QuarantinedAtUtc { get; set; }
        public long                      LastClaimSequence { get; set; }
        public string?                   LastError        { get; set; }
    }
}

internal enum SubmissionSpoolEnqueueDisposition
{
    Enqueued,
    AlreadyQueued,
    ConflictingSubmissionId,
    CapacityExceeded,
}

internal readonly record struct SubmissionSpoolEnqueueResult(Guid SubmissionId,
                                                              SubmissionSpoolEnqueueDisposition Disposition);

internal sealed record RunSubmissionSpoolLease(Guid SubmissionId,
                                                string LeaseToken,
                                                SubmitRunRequest Request,
                                                int AttemptCount);

internal readonly record struct SubmissionSpoolSnapshot(int TotalCount, int PendingCount, int QuarantinedCount);
