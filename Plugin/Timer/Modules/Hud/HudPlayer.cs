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
using Source2Surf.Timer.Modules.Zone;
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

    public bool                           Frozen;       // the player's camera holds their view still
    public CEntityHandle<IBasePlayerPawn> Pawn;         // whose view it holds
    public CEntityHandle<IBaseEntity>     PreviousView; // a map camera that held their view before the drag, to hand back
}

/// <summary>
///     The replay menu (!replay): what's picked, and a Watch on its way. Selected indexes the leaderboard.
/// </summary>
internal sealed class HudReplayMenu
{
    public bool    Open;
    public int     Tab;   // 0 the leaderboard, 1 the player's own runs
    public int     Track;
    public int     Stage; // 0 is the full run
    public int     Style;
    public int     Page;
    public int     Selected;
    public bool    Loading;
    public string? Note; // why the last Watch didn't start

    // My runs, as last loaded, and for which pick.
    public IReadOnlyList<RunRecord>?            Runs;
    public (int Style, int Track, int Stage)    RunsFor;
    public bool                                 RunsLoading;
}

/// <summary>
///     The profile card (!profile): whose it is, its picks, and what's been fetched for it. Version tells a late
///     answer for an earlier profile from one for this one.
/// </summary>
/// <summary>
///     The admin zone panel (!zone). The zone module keeps the zones and the zone being placed.
/// </summary>
/// <summary>
///     The records panel (!wr / !sr). The record module keeps the boards.
/// </summary>
/// <summary>
///     The style picker (!style).
/// </summary>
/// <summary>
///     The map info card (!mapinfo).
/// </summary>
internal sealed class HudMapInfo
{
    public bool Open;
    public bool Dirty;
    public int  Version = -1; // the record module's RecordsVersion, when last drawn
}

internal sealed class HudStyles
{
    public bool    Open;
    public int     Page;
    public int     Picked;                          // a style id: the current one until another is clicked
    public (string Id, float At)? LastClick;        // for the double-click
    public bool    Dirty;
    public readonly int[] RowIds = new int[HudModule.StyleRows];
}

internal sealed class HudRecords
{
    public bool    Open;
    public string? Map;          // another map's name; null for this one
    public int     Style;
    public int     Track;
    public int     Stage;
    public int     Page;
    public long?   Picked;       // the picked run's id
    public bool    PickFirst;    // pick the board's first run once it's there
    public long?   Confirm;      // the run whose Delete was clicked once
    public long?   Deleting;     // asked to delete; the note says so once it's gone
    public bool    ShowDeleted;  // the note follows the deleted run's player: their next run takes the row
    public ulong   DeletedSteamId;
    public string? DeletedName;
    public string? DeletedTime;
    public string? Note;
    public bool    Warn;
    public bool    Dirty;
    public int     Version = -1; // the record module's RecordsVersion, when last drawn
    public readonly long[] RowIds = new long[HudModule.RecordRows];
}

internal sealed class HudZones
{
    public bool            Open;
    public int             Track;
    public EZoneType       Type = EZoneType.Stage;
    public int             Number;                  // for stages and checkpoints; 0 until the panel first opens
    public int             Page;
    public uint?           Confirm;                 // the zone whose Delete was clicked once
    public uint?           Added;                   // the zone just placed from the panel, picked out in the list
    public uint?           Here;                    // the zone last teleported to
    public string?         Note;
    public bool            Warn;
    public bool            Dirty;
    public int             Version = -1;            // the zone module's EditVersion, when last read
    public ZoneBuildState? Build;                   // the zone being placed, as last read
    public bool            FromPanel;               // it was started from the panel, which comes back when it's placed
    public readonly HashSet<uint> Known = [];       // zone ids as last read, to spot the one just placed
    public bool            KnownValid;
    public readonly uint[] RowIds = new uint[HudModule.ZoneRows];
    public bool            PromptShown;
    public bool            PromptRecheck;           // flipped each time the prompt shows, so its key cap looks the key up again
}

/// <summary>
///     What the HUD last drew of the map chooser, which keeps the state itself.
/// </summary>
internal sealed class HudChooser
{
    public int           Version = -1; // the chooser's, when last drawn
    public NominateMenu? Menu;         // the nominate menu as last read; null while closed
    public int           Page;
    public string?       Note;         // why the last nomination didn't go through
    public bool          Dirty;        // a page turn or a note to draw
    public bool          VoteShown;
    public bool          VoteRecheck;  // flipped each time the vote panel shows, so its key caps look their keys up again
    public int           VoteSecond  = -1; // the countdown as last drawn, so it's only formatted when it changes
}

internal sealed class HudProfile
{
    public bool       Open;
    public int        Version;
    public PlayerSlot Target;
    public ulong      SteamId;
    public int        Style;
    public int        Track;

    // Opened by SteamID for someone not on the server: all from the backend, with their PBs here in Records.
    public bool                      Offline;
    public IReadOnlyList<RunRecord>? Records;

    public int RankOf; // 0 until fetched (or when the player isn't ranked)
    public int Rank;

    public PlayerSummary? Summary;
    public bool           SummaryFetched; // a provider without the query answers null

    public float MapPlayTime;
    public int   MapPlays;
    public bool  MapStatsFetched;
}

/// <summary>
///     One recent split. <see cref="Time" /> is what the row shows (a stage's own time, or the run's time at a
///     checkpoint) and <see cref="Pb" /> / <see cref="Wr" /> what it's compared against, as they were then.
///     The Cum* fields are the run's time at the split, for comparing the whole run. It's a stage's or a checkpoint's,
///     named in each viewer's language when shown.
/// </summary>
internal sealed record HudSplit(bool Stage, int Number, float Time, float? Pb, float? Wr, float Cum, float? CumPb, float? CumWr);

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
        NextHudAt   = now + ((slot % 6) / 64f);
        TrSettledAt = now + 30f;

        Array.Fill(UnplaceAt, float.NaN);
    }

    public PlayerSlot Slot { get; }

    public ulong SteamId;
    public bool  SettingsLoaded;
    public bool  SettingsChanged; // this visit, so a late load doesn't undo it
    public float SaveAt = float.NaN;

    // ---- the player's own custom_hud_layout, and what it currently holds
    public ICustomHudLayout? Layout; // use only while IsValid()
    public float                           NextLayoutAttempt;

    public readonly Dictionary<(string Panel, string Name), string> SentText     = [];
    public readonly Dictionary<(string Panel, string Name), bool>   SentClass    = [];
    public readonly Dictionary<(string Panel, string Prefix), string> SentNumbered = [];

    // ---- what the splits and records lines were last drawn from, so unchanged ones aren't formatted again
    public (bool Drawn, HudPlayer? Run, int Serial, int Cleared, int Compare, int Limit, bool Fade, bool MenuOpen, int Epoch) SplitsDrawn;
    public (bool Drawn, int Style, int Track, RunRecord? Wr, RunRecord? Pb, int Version, int Epoch)                        RecordsDrawn;

    // ---- this player's translations. Their language arrives a second or so after joining (a cl_language query)
    // and can't change after, so the cache is only cleared until TrSettledAt.
    public readonly Dictionary<string, string?> TrCache = [];
    public          float                       TrCacheUntil;
    public          float                       TrSettledAt;
    public          int                         TrEpoch;

    public void ForgetSent()
    {
        SplitsDrawn  = default;
        RecordsDrawn = default;
        SsjDrawn     = null;
        SentText.Clear();
        SentClass.Clear();
        SentNumbered.Clear();
        Chooser.Version     = -1;
        Chooser.VoteSecond  = -1;
        Zones.Version       = -1;
        Records.Version     = -1;
        MapInfo.Version     = -1;
        Zones.PromptShown   = false;
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

    public readonly HudReplayMenu Replays = new ();
    public readonly HudProfile    Profile = new ();

    public readonly HudChooser Chooser = new ();

    public readonly HudZones   Zones   = new ();
    public readonly HudRecords Records = new ();
    public readonly HudStyles  Styles  = new ();
    public readonly HudMapInfo MapInfo = new ();

    // Any menu takes the mouse.
    public bool AnyMenuOpen => MenuOpen || Replays.Open || Profile.Open || Chooser.Menu is not null || Zones.Open || Records.Open || Styles.Open || MapInfo.Open;

    // HUD settings, the nominate menu and the zone panel also keep the player from moving.
    public bool MovementLocked => MenuOpen || Chooser.Menu is not null || Zones.Open;

    // The texts this player reads, in their language where there's a translation.
    public HudTr Tr;

    // The saved-locations panel, which walk + inspect shows and hides and the first saved location shows; for this
    // visit only.
    public bool LocsShown;
    public int  LocsCount;    // saved locations at the last refresh
    public bool LocsWasShown; // on the last refresh
    public bool LocsRecheck;  // flipped each time it shows, so its key caps look their keys up again

    // Holds the view still while they drag. It's switched off after a drag rather than removed, and reused: a
    // camera removed while it holds the view never tells the client it let go, which leaves them looking through it.
    public ICustomPlayerCamera? Camera;

    // ---- refresh pacing
    public float  NextHudAt;
    public float  LastSpeed;
    public int    SpeedTrend; // -1 slowing, 0 steady, 1 gaining (drives the speed colour)

    // ---- this player's run, as the HUD shows it
    public readonly List<HudSplit> Splits = []; // newest first
    public          int            SplitSerial;  // counts splits, so a HUD showing them knows one arrived
    public          int            SplitCleared; // the serial when the run was cleared: no split since to animate

    // ---- the run this player's HUD shows splits from (theirs, or a spectated player's), and its last animated split
    public HudPlayer? SplitsFrom;
    public int        SplitsSeen;

    public HudFinish?      Finish;
    public HudStageResult? LastStage;  // the last stage finished, for the finish summary
    public bool            Stopped;    // the run stopped short of the end, outside the start zone
    public ETimerStatus    LastStatus;

    public EZoneType ZoneType = EZoneType.Invalid; // the start, stage or end zone the player is standing in
    public int       ZoneTrack;
    public int       ZoneData;                     // the stage number of a stage zone

    // ---- SSJ: this player's jumps, and the jump their panel shows (theirs, or a spectated player's)
    public readonly SsjTracker  Ssj = new ();
    public          SsjTracker? SsjFrom;
    public          int         SsjSeen;
    public          SsjJump?    SsjShown;
    public          float       SsjShownAt; // game time it was picked; it fades out a few seconds later
    public          bool        SsjSnap;    // as last sent: transitions off, from faded out until it shows again
    public          bool        SsjWasGone;
    public          (SsjJump Jump, int Rows, int Epoch)? SsjDrawn; // what the card's texts were written for

    // The live player whose keys and jumps this HUD shows (itself, or one spectated); -1 for none or a replay bot.
    public int Watched = -1;

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
        Stopped      = false;
        SplitCleared = SplitSerial;
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
