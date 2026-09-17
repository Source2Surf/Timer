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
using MagicOnion;
using MessagePack;

namespace Source2Surf.Timer.Backend.Rpc.Contracts
{

/// <summary>
/// Identifies whether a submitted run is a main run or a stage run.
/// Numeric values are part of the wire contract and must remain append-only.
/// </summary>
public enum RunKind : byte
{
    Main = 0,
    Stage = 1,
}

/// <summary>
/// Outcome of the run evaluation. The ordering mirrors the existing write API.
/// Numeric values are part of the wire contract and must remain append-only.
/// </summary>
public enum AttemptResult : byte
{
    NoNewRecord = 0,
    NewPersonalRecord = 1,
    NewServerRecord = 2,
}

/// <summary>
/// Indicates whether a submission was newly applied or was already applied by
/// an earlier retry.
/// Numeric values are part of the wire contract and must remain append-only.
/// </summary>
public enum SubmissionDisposition : byte
{
    Accepted = 0,
    AlreadyApplied = 1,
}

/// <summary>
/// Indicates whether the returned rank is available in the core submission result.
/// Numeric values are part of the wire contract and must remain append-only.
/// </summary>
public enum RankState : byte
{
    NotApplicable = 0,
    Pending = 1,
    Ready = 2,
}

/// <summary>
/// The four velocity samples carried by a run or checkpoint.
/// </summary>
[MessagePackObject]
public sealed class MotionDto
{
    [Key(0)]
    public float StartX { get; set; }

    [Key(1)]
    public float StartY { get; set; }

    [Key(2)]
    public float StartZ { get; set; }

    [Key(3)]
    public float AverageX { get; set; }

    [Key(4)]
    public float AverageY { get; set; }

    [Key(5)]
    public float AverageZ { get; set; }

    [Key(6)]
    public float MaxX { get; set; }

    [Key(7)]
    public float MaxY { get; set; }

    [Key(8)]
    public float MaxZ { get; set; }

    [Key(9)]
    public float EndX { get; set; }

    [Key(10)]
    public float EndY { get; set; }

    [Key(11)]
    public float EndZ { get; set; }
}

/// <summary>
/// Telemetry for one checkpoint in a submitted run.
/// </summary>
[MessagePackObject]
public sealed class CheckpointDto
{
    [Key(0)]
    public uint Index { get; set; }

    [Key(1)]
    public long TimeMicros { get; set; }

    [Key(2)]
    public float Sync { get; set; }

    [Key(3)]
    public MotionDto Motion { get; set; } = new MotionDto();
}

/// <summary>
/// Idempotent run submission sent by the plugin to the write service.
/// </summary>
[MessagePackObject]
public sealed class SubmitRunRequest
{
    [Key(0)]
    public Guid SubmissionId { get; set; }

    [Key(1)]
    public long SteamId { get; set; }

    [Key(2)]
    public string MapName { get; set; } = string.Empty;

    [Key(3)]
    public RunKind RunKind { get; set; }

    [Key(4)]
    public int Style { get; set; }

    [Key(5)]
    public int Track { get; set; }

    [Key(6)]
    public int Stage { get; set; }

    [Key(7)]
    public long TimeMicros { get; set; }

    [Key(8)]
    public int Jumps { get; set; }

    [Key(9)]
    public int Strafes { get; set; }

    [Key(10)]
    public float Sync { get; set; }

    [Key(11)]
    public MotionDto Motion { get; set; } = new MotionDto();

    [Key(12)]
    public CheckpointDto[] Checkpoints { get; set; } = Array.Empty<CheckpointDto>();

    [Key(13)]
    public long FinishedAtUnixTimeMilliseconds { get; set; }

    [Key(14)]
    public int ContractVersion { get; set; }

    [Key(15)]
    public int RulesetVersion { get; set; }

}

/// <summary>
/// Stable result returned for both a newly accepted submission and an idempotent retry.
/// </summary>
[MessagePackObject]
public sealed class SubmitRunResponse
{
    [Key(0)]
    public Guid SubmissionId { get; set; }

    [Key(1)]
    public ulong RunId { get; set; }

    [Key(2)]
    public AttemptResult AttemptResult { get; set; }

    [Key(3)]
    public int Rank { get; set; }

    [Key(4)]
    public long ReceivedAtUnixTimeMilliseconds { get; set; }

    [Key(5)]
    public SubmissionDisposition Disposition { get; set; }

    // Appended after the original response fields to preserve v1 key assignments.
    [Key(6)]
    public RankState RankState { get; set; }
}

/// <summary>
/// Request for confirming the result of a submission after a timeout.
/// </summary>
[MessagePackObject]
public sealed class GetSubmissionStatusRequest
{
    [Key(0)]
    public Guid SubmissionId { get; set; }
}

/// <summary>
/// Result of a submission status lookup.
/// </summary>
[MessagePackObject]
public sealed class GetSubmissionStatusResponse
{
    [Key(0)]
    public bool Found { get; set; }

    [Key(1)]
    public SubmitRunResponse? Submission { get; set; }
}

/// <summary>
/// Request to create a player profile when absent, or refresh its display name
/// when it already exists.
/// </summary>
[MessagePackObject]
public sealed class EnsurePlayerProfileRequest
{
    [Key(0)]
    public long SteamId { get; set; }

    [Key(1)]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Stable public projection of a player profile. It deliberately contains only scalar values
/// and no storage or game-server implementation types.
/// </summary>
[MessagePackObject]
public sealed class EnsurePlayerProfileResponse
{
    [Key(0)]
    public long PlayerId { get; set; }

    [Key(1)]
    public long SteamId { get; set; }

    [Key(2)]
    public string Name { get; set; } = string.Empty;

    [Key(3)]
    public uint Points { get; set; }

    [Key(4)]
    public long JoinDateUnixTimeMilliseconds { get; set; }

    [Key(5)]
    public long LastSeenDateUnixTimeMilliseconds { get; set; }
}

/// <summary>
/// Versioned MagicOnion write contract for timer run submissions.
/// </summary>
public interface ITimerWriteServiceV1 : IService<ITimerWriteServiceV1>
{
    UnaryResult<EnsurePlayerProfileResponse> EnsurePlayerProfileAsync(EnsurePlayerProfileRequest request);

    UnaryResult<SubmitRunResponse> SubmitRunAsync(SubmitRunRequest request);

    UnaryResult<GetSubmissionStatusResponse> GetSubmissionStatusAsync(GetSubmissionStatusRequest request);
}

}
