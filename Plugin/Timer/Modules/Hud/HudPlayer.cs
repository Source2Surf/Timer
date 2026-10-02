/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
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
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules.Hud;

/// <summary>
///     A dragged panel's centre offset in percent of the screen; <see cref="Cx" /> / <see cref="Cy" /> mean
///     snapped to the centre line.
/// </summary>
internal sealed class HudPosition
{
    public float X;
    public float Y;
    public bool  Cx;
    public bool  Cy;

    public HudPosition Copy()
        => new () { X = X, Y = Y, Cx = Cx, Cy = Cy };
}

internal sealed class HudDragState
{
    public required HudTarget    Target;
    public required HudPosition? Before;   // where it was before this drag, for cancel
    public required Vector       View;     // view angles to give back when the drag ends
    public          float        LastYaw;
    public          float        LastPitch;
    public          float        ArmedAt;  // game time from which the place/cancel/default keys count
    public          float        NextSendAt;
    public          int          Sends;
    public          bool         Smooth;   // whether the panel glides between updates
    public          float?       SnapSince; // set while the pickup's pitch levelling is on its way to the client
    public          Vector       Aim;       // the client's own view angles, which keep turning behind the frozen camera

    public ICustomPlayerCamera?       Camera;       // holds the player's view still while they drag
    public CEntityHandle<IBaseEntity> PreviousView; // their view entity before the drag (a map camera), to hand back
}

/// <summary>
///     One recent split. <see cref="Time" /> is what the row shows (a stage's own time, or the run's time at a
///     checkpoint) and <see cref="Pb" /> / <see cref="Wr" /> what it's compared against, as they were then.
///     The Cum* fields are the run's time at the split, for comparing the whole run.
/// </summary>
internal sealed record HudSplit(string Name, float Time, float? Pb, float? Wr, float Cum, float? CumPb, float? CumWr);

internal sealed record HudStageResult(int Stage, float Time, float? Pb, float? Wr);

/// <summary>
///     The last finished run, shown until the next one starts. PB / WR values are the ones it was compared
///     against, taken before saving could replace them.
/// </summary>
internal sealed record HudFinish(
    int             Track,
    float           Time,
    float?          Pb,
    float?          Wr,
    HudStageResult? Stage,
    int             End,
    int?            EndPb,
    int?            EndWr,
    bool            Practice);

/// <summary>
///     Everything the HUD keeps for one real player: their layout entity and what it holds, their settings, the
///     menu and drag state, and the run details the timer doesn't keep (splits, the last finish).
/// </summary>
internal sealed class HudPlayer
{
    public const int MaxSplits = 8;

    public HudPlayer(PlayerSlot slot, float now)
    {
        Slot = slot;

        // Spread players over frames so their refreshes don't all land at once.
        NextHudAt = now + ((slot % 6) / 64f);

        Array.Fill(UnplaceAt, float.NaN);
    }

    public PlayerSlot Slot { get; }

    public ulong SteamId;
    public bool  SettingsLoaded;
    public float SaveAt = float.NaN;

    // ---- the player's own custom_hud_layout, and what it currently holds
    public ICustomHudLayout? Layout; // use only while IsValid()
    public float                           NextLayoutAttempt;

    public readonly Dictionary<(string Panel, string Name), string> SentText     = [];
    public readonly Dictionary<(string Panel, string Name), bool>   SentClass    = [];
    public readonly Dictionary<(string Panel, string Prefix), string> SentNumbered = [];

    public void ForgetSent()
    {
        SentText.Clear();
        SentClass.Clear();
        SentNumbered.Clear();
    }

    // ---- settings
    public int[]          Settings  = HudOptions.NewSettings();
    public HudLine[]      Order     = (HudLine[]) HudLines.DefaultOrder.Clone();
    public HudPosition?[] Positions = new HudPosition?[HudTargets.Count];

    public void ResetSettings()
    {
        Settings  = HudOptions.NewSettings();
        Order     = (HudLine[]) HudLines.DefaultOrder.Clone();
        Positions = new HudPosition?[HudTargets.Count];
    }

    public bool IsOn(HudOption option)
        => HudOptions.IsOn(Settings, option);

    public string Setting(HudOption option)
        => option.Choices[Settings[option.Index]].Label;

    // ---- menu and dragging
    public int           Tab;
    public bool          MenuOpen;
    public HudDragState? Drag;

    // The menu (and the panels' positions, sizes and on/off) only change on a click, a command or a drag, so it's
    // rebuilt only after one: when MenuDirty is set, or until MenuBusyUntil while a placed panel finishes gliding.
    public bool  MenuDirty = true;
    public float MenuBusyUntil;

    public readonly float[] SmoothUntil = new float[HudTargets.Count]; // keeps a just-placed panel's glide on briefly
    public readonly float[] UnplaceAt   = new float[HudTargets.Count]; // when a gliding panel goes back to its CSS layout

    // ---- refresh pacing
    public float  NextHudAt;
    public float  NextSyncAt;
    public string SyncText = "100.00";
    public float  LastSpeed;
    public int    SpeedTrend; // -1 slowing, 0 steady, 1 gaining (drives the speed colour)

    // ---- this player's run, as the HUD shows it
    public readonly List<HudSplit> Splits = []; // newest first
    public          int            SplitSerial; // counts splits, so the HUD knows one arrived...
    public          int            SplitShown;  // ...and which one it last animated

    public HudFinish?      Finish;
    public HudStageResult? LastStage;  // the last stage finished, for the finish summary
    public bool            Stopped;    // the run stopped short of the end, outside the start zone
    public ETimerStatus    LastStatus;

    public EZoneType ZoneType = EZoneType.Invalid; // the start, stage or end zone the player is standing in
    public int       ZoneTrack;
    public int       ZoneData;                     // the stage number of a stage zone

    /// <summary>
    ///     The player's PB checkpoint splits, which the record cache doesn't keep, fetched per style and track.
    /// </summary>
    public (int Style, int Track, long RecordId, IReadOnlyList<RunCheckpoint>? Checkpoints) PbCheckpoints = (-1, -1, 0, null);

    /// <summary>
    ///     A new attempt: no result, no splits, and nothing left to animate.
    /// </summary>
    public void ClearRun()
    {
        Finish     = null;
        LastStage  = null;
        Stopped    = false;
        SplitShown = SplitSerial;
        Splits.Clear();
    }

    public void AddSplit(HudSplit split)
    {
        Splits.Insert(0, split);

        if (Splits.Count > MaxSplits)
        {
            Splits.RemoveAt(Splits.Count - 1);
        }

        SplitSerial++;
    }
}
