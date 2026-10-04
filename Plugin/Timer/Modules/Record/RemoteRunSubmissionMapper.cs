/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Managers.Submission;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Modules.Record;

/// <summary>
/// The plugin-owned, non-score policy fields required by a v1 run submission. Score policy,
/// including the style multiplier, deliberately does not belong here: the backend resolves it
/// from backend configuration.
/// </summary>
internal sealed class RemoteRunSubmissionOptions
{
    internal const string SectionName = BackendOptions.SectionName;
    internal const int ContractVersion = 1;

    private RemoteRunSubmissionOptions(int rulesetVersion)
    {
        RulesetVersion = rulesetVersion;
    }

    public int RulesetVersion { get; }

    /// <summary>
    /// Defaults to ruleset v1, which can be overridden explicitly.
    /// </summary>
    public static RemoteRunSubmissionOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        ValidateKnownSettings(section);
        var rulesetVersion = string.IsNullOrWhiteSpace(section["ruleset_version"])
                                 ? 1
                                 : ParsePositiveInteger(section["ruleset_version"], "ruleset_version");
        return new RemoteRunSubmissionOptions(rulesetVersion);
    }

    private static void ValidateKnownSettings(IConfigurationSection section)
    {
        foreach (var child in section.GetChildren())
        {
            if (BackendOptions.Keys.Contains(child.Key))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"{SectionName}:{child.Key} is not supported; score policy is backend-owned.");
        }
    }

    private static int ParsePositiveInteger(string? raw, string setting)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{setting} must be a positive integer.");
        }

        return value;
    }

}

/// <summary>
/// Maps the plugin's run facts to the versioned run-submission contract. Score policy is deliberately
/// absent from this mapper and is resolved by the backend.
/// </summary>
internal static class RemoteRunSubmissionMapper
{
    private const long MicrosPerSecond = 1_000_000;
    private const long MaximumTimeMicros = 24L * 60 * 60 * MicrosPerSecond;

    public static SubmitRunRequest CreateMain(ulong                      steamId,
                                              string                     mapName,
                                              RecordRequest              record,
                                              DateTime                   finishedAtUtc,
                                              RemoteRunSubmissionOptions options)
        => Create(steamId, mapName, record, RunKind.Main, finishedAtUtc, options);

    public static SubmitRunRequest CreateStage(ulong                      steamId,
                                               string                     mapName,
                                               RecordRequest              record,
                                               DateTime                   finishedAtUtc,
                                               RemoteRunSubmissionOptions options)
        => Create(steamId, mapName, record, RunKind.Stage, finishedAtUtc, options);

    public static bool IsCanonicalAcknowledgement(SubmitRunRequest request, SubmitRunResponse? response)
    {
        ArgumentNullException.ThrowIfNull(request);

        return response is not null
               && response.SubmissionId == request.SubmissionId
               && request.SteamId > 0
               && response.RunId != 0
               && response.RunId <= long.MaxValue
               && response.ReceivedAtUnixTimeMilliseconds > 0
               && response.Disposition is SubmissionDisposition.Accepted or SubmissionDisposition.AlreadyApplied
               && response.AttemptResult is AttemptResult.NoNewRecord
                   or AttemptResult.NewPersonalRecord
                   or AttemptResult.NewServerRecord
               && response.RankState is RankState.NotApplicable or RankState.Pending or RankState.Ready
               && response.Rank >= 0;
    }

    /// <summary>
    /// Produces the event/cache projection only after a canonical backend acknowledgement. The
    /// caller must supply the map identity captured at finish time; v1 intentionally does not
    /// return a map id, so publishing a synthetic record with map id zero is unsafe for replay
    /// matching.
    /// </summary>
    public static RemoteAcknowledgedRun ToAcknowledgedRun(SubmitRunRequest request,
                                                           SubmitRunResponse response,
                                                           string            playerName,
                                                           ulong             mapId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        if (mapId == 0)
        {
            throw new InvalidOperationException("Cannot publish a remote record acknowledgement without a map identity.");
        }

        if (!IsCanonicalAcknowledgement(request, response))
        {
            throw new InvalidOperationException("Remote write service returned a non-canonical run acknowledgement.");
        }

        var time = ToSeconds(request.TimeMicros, "TimeMicros");
        var runDate = DateTimeOffset.FromUnixTimeMilliseconds(request.FinishedAtUnixTimeMilliseconds).UtcDateTime;
        var motion = request.Motion ?? throw new InvalidOperationException("Remote submission motion was unexpectedly null.");
        var record = new RunRecord
        {
            Id = checked((long)response.RunId),
            RunDate = runDate,
            SteamId = checked((ulong)request.SteamId),
            PlayerName = playerName ?? string.Empty,
            MapId = mapId,
            Style = request.Style,
            Track = request.Track,
            Stage = request.Stage,
            Time = time,
            Jumps = request.Jumps,
            Strafes = request.Strafes,
            Sync = request.Sync,
            VelocityStartX = motion.StartX,
            VelocityStartY = motion.StartY,
            VelocityStartZ = motion.StartZ,
            VelocityAvgX = motion.AverageX,
            VelocityAvgY = motion.AverageY,
            VelocityAvgZ = motion.AverageZ,
            VelocityEndX = motion.EndX,
            VelocityEndY = motion.EndY,
            VelocityEndZ = motion.EndZ,
        };

        return new RemoteAcknowledgedRun((EAttemptResult)response.AttemptResult, record, response.Rank);
    }

    private static SubmitRunRequest Create(ulong                      steamId,
                                           string                     mapName,
                                           RecordRequest              record,
                                           RunKind                    runKind,
                                           DateTime                   finishedAtUtc,
                                           RemoteRunSubmissionOptions options)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(options);

        if (steamId == 0 || steamId > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(steamId), "SteamId must fit in a positive signed 64-bit integer.");
        }

        ValidateMapName(mapName);
        ValidateRunShape(record, runKind);
        ValidateFinishedAtUtc(finishedAtUtc);

        var timeMicros = ToMicros(record.Time, "Time");
        var checkpoints = CreateCheckpoints(record.Checkpoints, timeMicros);
        var motion = CreateMotion(record, "Motion");

        return new SubmitRunRequest
        {
            SubmissionId = Guid.NewGuid(),
            SteamId = checked((long)steamId),
            MapName = mapName,
            RunKind = runKind,
            Style = record.Style,
            Track = record.Track,
            Stage = record.Stage,
            TimeMicros = timeMicros,
            Jumps = record.Jumps,
            Strafes = record.Strafes,
            Sync = record.Sync,
            Motion = motion,
            Checkpoints = checkpoints,
            FinishedAtUnixTimeMilliseconds = new DateTimeOffset(finishedAtUtc).ToUnixTimeMilliseconds(),
            ContractVersion = RemoteRunSubmissionOptions.ContractVersion,
            RulesetVersion = options.RulesetVersion,
        };
    }

    private static void ValidateMapName(string? mapName)
    {
        if (string.IsNullOrEmpty(mapName) || mapName.Length > 192 || mapName != mapName.Trim())
        {
            throw new ArgumentException("Map name must contain 1 to 192 non-whitespace characters.", nameof(mapName));
        }

        foreach (var character in mapName)
        {
            if (!(character is >= 'a' and <= 'z'
                  or >= 'A' and <= 'Z'
                  or >= '0' and <= '9'
                  or '.' or '_' or '-'))
            {
                throw new ArgumentException("Map name contains an unsupported character.", nameof(mapName));
            }
        }
    }

    private static void ValidateRunShape(RecordRequest record, RunKind runKind)
    {
        if ((uint)record.Style >= TimerConstants.MAX_STYLE
            || (uint)record.Track >= TimerConstants.MAX_TRACK)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "Style or track is outside the supported range.");
        }

        if (runKind == RunKind.Main && record.Stage != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "Main submissions must have stage zero.");
        }

        if (runKind == RunKind.Stage && record.Stage is < 1 or >= TimerConstants.MAX_STAGE)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "Stage submissions must have a supported stage index.");
        }

        if (record.Jumps < 0 || record.Strafes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(record), "Jumps and strafes must not be negative.");
        }

        ValidateSync(record.Sync, "Sync");
    }

    private static void ValidateFinishedAtUtc(DateTime finishedAtUtc)
    {
        if (finishedAtUtc == default || finishedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentOutOfRangeException(nameof(finishedAtUtc),
                                                  "FinishedAtUtc must be a non-default UTC timestamp.");
        }
    }

    private static CheckpointDto[] CreateCheckpoints(IReadOnlyList<RecordRequest.CheckpointRecord>? checkpoints,
                                                       long                                        totalTimeMicros)
    {
        if (checkpoints is null)
        {
            throw new ArgumentException("Checkpoints must not be null.", nameof(checkpoints));
        }

        if (checkpoints.Count > TimerConstants.MAX_STAGE - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoints), "Too many checkpoints for the remote submission contract.");
        }

        var result = new CheckpointDto[checkpoints.Count];
        var previousIndex = 0;
        var previousTimeMicros = 0L;
        for (var index = 0; index < checkpoints.Count; index++)
        {
            var checkpoint = checkpoints[index]
                             ?? throw new ArgumentException("Checkpoint must not be null.", nameof(checkpoints));
            if (checkpoint.CheckpointIndex <= previousIndex
                || checkpoint.CheckpointIndex >= TimerConstants.MAX_STAGE)
            {
                throw new ArgumentOutOfRangeException(nameof(checkpoints),
                                                      "Checkpoint indices must be strictly increasing and in range.");
            }

            var timeMicros = ToMicros(checkpoint.Time, "Checkpoint.Time");
            if (timeMicros <= previousTimeMicros || timeMicros > totalTimeMicros)
            {
                throw new ArgumentOutOfRangeException(nameof(checkpoints),
                                                      "Checkpoint times must be strictly increasing and no later than the run time.");
            }

            ValidateSync(checkpoint.Sync, "Checkpoint.Sync");
            result[index] = new CheckpointDto
            {
                Index = checked((uint)checkpoint.CheckpointIndex),
                TimeMicros = timeMicros,
                Sync = checkpoint.Sync,
                Motion = CreateMotion(checkpoint, "Checkpoint.Motion"),
            };
            previousIndex = checkpoint.CheckpointIndex;
            previousTimeMicros = timeMicros;
        }

        return result;
    }

    private static MotionDto CreateMotion(RecordRequest record, string fieldName)
        => CreateMotion(record.VelocityStartX,
                        record.VelocityStartY,
                        record.VelocityStartZ,
                        record.VelocityAvgX,
                        record.VelocityAvgY,
                        record.VelocityAvgZ,
                        record.VelocityMaxX,
                        record.VelocityMaxY,
                        record.VelocityMaxZ,
                        record.VelocityEndX,
                        record.VelocityEndY,
                        record.VelocityEndZ,
                        fieldName);

    private static MotionDto CreateMotion(RecordRequest.CheckpointRecord checkpoint, string fieldName)
        => CreateMotion(checkpoint.VelocityStartX,
                        checkpoint.VelocityStartY,
                        checkpoint.VelocityStartZ,
                        checkpoint.VelocityAvgX,
                        checkpoint.VelocityAvgY,
                        checkpoint.VelocityAvgZ,
                        checkpoint.VelocityMaxX,
                        checkpoint.VelocityMaxY,
                        checkpoint.VelocityMaxZ,
                        checkpoint.VelocityEndX,
                        checkpoint.VelocityEndY,
                        checkpoint.VelocityEndZ,
                        fieldName);

    private static MotionDto CreateMotion(float startX,
                                          float startY,
                                          float startZ,
                                          float averageX,
                                          float averageY,
                                          float averageZ,
                                          float maxX,
                                          float maxY,
                                          float maxZ,
                                          float endX,
                                          float endY,
                                          float endZ,
                                          string fieldName)
    {
        ValidateFinite(startX, $"{fieldName}.StartX");
        ValidateFinite(startY, $"{fieldName}.StartY");
        ValidateFinite(startZ, $"{fieldName}.StartZ");
        ValidateFinite(averageX, $"{fieldName}.AverageX");
        ValidateFinite(averageY, $"{fieldName}.AverageY");
        ValidateFinite(averageZ, $"{fieldName}.AverageZ");
        ValidateFinite(maxX, $"{fieldName}.MaxX");
        ValidateFinite(maxY, $"{fieldName}.MaxY");
        ValidateFinite(maxZ, $"{fieldName}.MaxZ");
        ValidateFinite(endX, $"{fieldName}.EndX");
        ValidateFinite(endY, $"{fieldName}.EndY");
        ValidateFinite(endZ, $"{fieldName}.EndZ");

        return new MotionDto
        {
            StartX = startX,
            StartY = startY,
            StartZ = startZ,
            AverageX = averageX,
            AverageY = averageY,
            AverageZ = averageZ,
            MaxX = maxX,
            MaxY = maxY,
            MaxZ = maxZ,
            EndX = endX,
            EndY = endY,
            EndZ = endZ,
        };
    }

    private static long ToMicros(float seconds, string fieldName)
    {
        if (!float.IsFinite(seconds) || seconds <= 0 || seconds > MaximumTimeMicros / (float)MicrosPerSecond)
        {
            throw new ArgumentOutOfRangeException(fieldName, "Run time must be finite, positive, and no longer than 24 hours.");
        }

        try
        {
            var micros = checked((long)Math.Round((double)seconds * MicrosPerSecond, MidpointRounding.AwayFromZero));
            if (micros <= 0 || micros > MaximumTimeMicros)
            {
                throw new ArgumentOutOfRangeException(fieldName, "Run time cannot be represented by the remote contract.");
            }

            return micros;
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(fieldName, "Run time cannot be represented by the remote contract.");
        }
    }

    private static float ToSeconds(long micros, string fieldName)
    {
        if (micros <= 0 || micros > MaximumTimeMicros)
        {
            throw new ArgumentOutOfRangeException(fieldName);
        }

        var seconds = micros / (double)MicrosPerSecond;
        if (seconds > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(fieldName);
        }

        return (float)seconds;
    }

    private static void ValidateSync(float value, string fieldName)
    {
        ValidateFinite(value, fieldName);
        if (value is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(fieldName, "Sync must be between zero and 100.");
        }
    }

    private static void ValidateFinite(float value, string fieldName)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(fieldName, "Value must be finite.");
        }
    }
}

/// <summary>
/// Narrow adapter around the sender's enqueue-before-wait boundary. Keeping it here lets the
/// record path test routing without replacing the transport or sender lifecycle.
/// </summary>
internal sealed class RemoteRunSubmissionWriter
{
    private readonly Func<SubmitRunRequest, CancellationToken, Task<SubmitRunResponse>> _enqueueAndWaitAsync;

    public RemoteRunSubmissionWriter(RunSubmissionSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _enqueueAndWaitAsync = sender.EnqueueAndWaitAsync;
    }

    internal RemoteRunSubmissionWriter(
        Func<SubmitRunRequest, CancellationToken, Task<SubmitRunResponse>> enqueueAndWaitAsync)
        => _enqueueAndWaitAsync = enqueueAndWaitAsync ?? throw new ArgumentNullException(nameof(enqueueAndWaitAsync));

    public async Task<SubmitRunResponse> EnqueueAndWaitAsync(SubmitRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = await _enqueueAndWaitAsync(request, cancellationToken).ConfigureAwait(false);
        if (!RemoteRunSubmissionMapper.IsCanonicalAcknowledgement(request, response))
        {
            throw new InvalidOperationException("Remote write sender completed with a non-canonical acknowledgement.");
        }

        return response;
    }
}

internal readonly record struct RemoteAcknowledgedRun(EAttemptResult RecordType, RunRecord SavedRecord, int Rank);
