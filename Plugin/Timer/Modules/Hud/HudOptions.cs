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
using System.Globalization;
using System.Linq;

namespace Source2Surf.Timer.Modules.Hud;

/// <summary>
///     How a value pill is coloured: on (green), off (outlined grey), or a plain pick.
/// </summary>
internal enum HudTone : byte
{
    None,
    On,
    Off,
}

/// <summary>
///     One choice of a setting. <see cref="Class" /> goes on each of the option's panels while chosen.
/// </summary>
internal readonly record struct HudChoice(string Label, string? Class = null, HudTone Tone = HudTone.None);

/// <summary>
///     A HUD setting. <see cref="Id" /> is the Button that changes it (a size stepper's buttons are
///     <c>&lt;Id&gt;Down</c> / <c>&lt;Id&gt;Up</c>) and <c>&lt;Id&gt;Value</c> the label showing it. The layout's
///     classes match each option's default. Saved as <see cref="Key" /> and the choice's index (PlayerSettingsCodec), so
///     a key is never reused and choices are only ever added at the end.
/// </summary>
internal sealed class HudOption
{
    public HudOption(byte key, string id, HudChoice[] choices, string[]? panels = null, int initial = 0)
    {
        Key     = key;
        Id      = id;
        ValueId = id + "Value";
        DownId  = id + "Down";
        UpId    = id + "Up";
        Choices = choices;
        Panels  = panels ?? [];
        Initial = initial;
    }

    public byte       Key     { get; }
    public int        Index   { get; set; }
    public string     Id      { get; }
    public string     ValueId { get; }
    public string     DownId  { get; }
    public string     UpId    { get; }
    public HudChoice[] Choices { get; }
    public string[]   Panels  { get; }
    public int        Initial { get; }

    /// <summary>
    ///     Changed with its <c>&lt;Id&gt;Down</c> / <c>&lt;Id&gt;Up</c> buttons rather than by clicking it, like a size.
    /// </summary>
    public bool Stepped { get; init; }

    /// <summary>
    ///     Greys the option out (shown as Off) while other settings make it meaningless.
    /// </summary>
    public Func<int[], bool>? Needs { get; init; }

    /// <summary>
    ///     False for a setting saved with the HUD's but changed by a chat command, with no button or label.
    /// </summary>
    public bool InMenu { get; init; } = true;

    public bool IsSize => ReferenceEquals(Choices, HudOptions.SizeChoices);

    public bool IsStepper => Stepped || IsSize;
}

internal static class HudOptions
{
    private static readonly HudChoice On  = new ("On", null, HudTone.On);
    private static readonly HudChoice Off = new ("Off", null, HudTone.Off);

    /// <summary>
    ///     What a greyed-out option shows.
    /// </summary>
    public static readonly HudChoice Disabled = Off;

    public static readonly HudChoice[] OnOff = [On, Off];

    /// <summary>
    ///     A panel's on/off: Off hides it.
    /// </summary>
    public static readonly HudChoice[] Shown = [On, new ("Off", "Hidden", HudTone.Off)];

    public static readonly int[] Sizes = [70, 80, 90, 100, 110, 120, 130, 140, 150];

    public static readonly HudChoice[] SizeChoices =
        Sizes.Select(n => new HudChoice($"{n}%", n == 100 ? null : $"size-{n}")).ToArray();

    public static readonly int SizeDefault = Array.IndexOf(Sizes, 100);

    // HUD tab: each panel's on/off and size
    public static readonly HudOption Run        = new (1, "OptRun", Shown, ["RunPanel"]);
    public static readonly HudOption SizeRun    = new (2, "SizeRun", SizeChoices, ["RunBody"], SizeDefault);
    public static readonly HudOption CSpeed     = new (3, "OptCSpeed", Shown, ["CSpeedPanel"]);
    public static readonly HudOption SizeCSpeed = new (4, "SizeCSpeed", SizeChoices, ["CSpeedBody"], SizeDefault);
    public static readonly HudOption Info       = new (5, "OptInfo", Shown, ["InfoPanel"]);
    public static readonly HudOption SizeInfo   = new (6, "SizeInfo", SizeChoices, ["InfoBody"], SizeDefault);
    public static readonly HudOption Splits     = new (7, "OptSplits", Shown, ["SplitsPanel"]);
    public static readonly HudOption SizeSplits = new (8, "SizeSplits", SizeChoices, ["SplitsBody"], SizeDefault);
    public static readonly HudOption Keys       = new (9, "OptKeys", Shown, ["KeysPanel"], 1); // off until !showkeys
    public static readonly HudOption SizeKeys   = new (10, "SizeKeys", SizeChoices, ["KeysBody"], SizeDefault);

    // Timer tab: the lines (switched from their rows) and what the time is compared against
    public static readonly HudOption Zone    = new (11, "OptZone", OnOff);
    public static readonly HudOption Mode    = new (12, "OptMode", OnOff);
    public static readonly HudOption Speed   = new (13, "OptSpeed", OnOff);
    public static readonly HudOption Start   = new (14, "OptStart", OnOff);
    public static readonly HudOption Sync    = new (15, "OptSync", OnOff);
    public static readonly HudOption Jumps   = new (16, "OptJumps", OnOff, initial: 1);
    public static readonly HudOption Strafes = new (17, "OptStrafes", OnOff, initial: 1);

    public static readonly HudOption Compare =
        new (18, "OptCompare", [new ("Personal best"), new ("Server record"), new ("Off", null, HudTone.Off)]);

    public const int ComparePersonalBest = 0;
    public const int CompareServerRecord = 1;
    public const int CompareOff          = 2;

    /// <summary>
    ///     The position-based difference against the replay of what the time is compared with (the player's PB, or
    ///     the server record), for the style and track they're on, continuously while running.
    /// </summary>
    public static readonly HudOption Live = new (19, "OptLive", OnOff)
    {
        Needs = settings => settings[Compare.Index] != CompareOff,
    };

    // Speed tab: the measure is shared by the timer's speed line and center speed; the colours are center speed's
    public static readonly HudOption SpeedColor = new (20, "OptSpeedColor", OnOff);
    public static readonly HudOption SpeedAxes  = new (21, "OptSpeedAxes", [new ("Horizontal"), new ("3D")]);

    // Splits tab
    public static readonly HudOption SplitRows = new (22, "OptSplitRows", [new ("3"), new ("5"), new ("8")], initial: 1);
    public static readonly HudOption SplitFade = new (23, "OptSplitFade", OnOff);

    // Keys tab
    public static readonly HudOption KeyMouse    = new (24, "OptKeyMouse", OnOff);
    public static readonly HudOption KeyJumpDuck = new (25, "OptKeyJumpDuck", OnOff);

    // SSJ tab: the panel (off until turned on there), which jumps it shows, and what it shows of them
    public static readonly HudOption Ssj     = new (26, "OptSsj", Shown, ["SsjPanel"], 1);
    public static readonly HudOption SizeSsj = new (27, "SizeSsj", SizeChoices, ["SsjBody"], SizeDefault);

    public static readonly HudOption SsjJump =
        new (28, "OptSsjJump", Enumerable.Range(1, 16).Select(n => new HudChoice(n.ToString(CultureInfo.InvariantCulture))).ToArray(), initial: 5)
        {
            Stepped = true,
        };

    // Jump 1 with repeat is every jump; jump 1 alone is the takeoff only, so first jump adds nothing to it.
    public static readonly HudOption SsjRepeat = new (29, "OptSsjRepeat", OnOff, initial: 1);
    public static readonly HudOption SsjFirst  = new (30, "OptSsjFirst", OnOff) { Needs = settings => settings[SsjJump.Index] > 0 };

    public static readonly HudOption SsjSpeedDiff  = new (31, "OptSsjSpeedDiff", OnOff);
    public static readonly HudOption SsjHeight     = new (32, "OptSsjHeight", OnOff, initial: 1);
    public static readonly HudOption SsjGain       = new (33, "OptSsjGain", OnOff);
    public static readonly HudOption SsjSync       = new (34, "OptSsjSync", OnOff);
    public static readonly HudOption SsjStrafes    = new (35, "OptSsjStrafes", OnOff, initial: 1);
    public static readonly HudOption SsjEfficiency = new (36, "OptSsjEfficiency", OnOff, initial: 1);

    // HUD tab, Player: not the HUD's, but saved with it (IPlayerSettings)
    public static readonly HudOption Hide   = new (37, "OptHide", OnOff, initial: 1);
    public static readonly HudOption Sounds = new (38, "OptSounds", OnOff);

    // HUD tab: the spectator list
    public static readonly HudOption Specs     = new (39, "OptSpecs", Shown, ["SpecPanel"]);
    public static readonly HudOption SizeSpecs = new (40, "SizeSpecs", SizeChoices, ["SpecBody"], SizeDefault);

    // HUD tab: every menu's size, for very low or high resolutions
    public static readonly HudOption SizeMenus =
        new (41, "SizeMenus", SizeChoices, ["Menu", "RMenu", "PfMenu", "NMenu", "ZnMenu", "LbMenu", "StyMenu", "MiMenu", "VotePanel", "ZnPrompt"], SizeDefault);

    // The saved locations panel. 42 and 47–82 are held by options on other branches.
    public static readonly HudOption SizeLocs = new (83, "SizeLocs", SizeChoices, ["LocsBody"], SizeDefault);

    // !footsteps and !stopsound
    public static readonly HudOption Footsteps    = new (43, "OptFootsteps", OnOff) { InMenu = false };
    public static readonly HudOption WeaponSounds = new (44, "OptWeaponSounds", OnOff) { InMenu = false };

    // !showzones
    public static readonly HudOption Zones = new (45, "OptZones", OnOff) { InMenu = false };

    // !country
    public static readonly HudOption Country = new (46, "OptCountry", OnOff) { InMenu = false };

    public static readonly HudOption[] All =
    [
        Run, SizeRun, CSpeed, SizeCSpeed, Info, SizeInfo, Splits, SizeSplits, Keys, SizeKeys,
        Zone, Mode, Speed, Start, Sync, Jumps, Strafes, Compare, Live,
        SpeedColor, SpeedAxes, SplitRows, SplitFade, KeyMouse, KeyJumpDuck,
        Ssj, SizeSsj, SsjJump, SsjRepeat, SsjFirst, SsjSpeedDiff, SsjHeight, SsjGain, SsjSync, SsjStrafes, SsjEfficiency,
        Hide, Sounds, Specs, SizeSpecs, SizeMenus, SizeLocs, Footsteps, WeaponSounds, Zones, Country,
    ];

    public static readonly IReadOnlyDictionary<string, HudOption> ById;
    public static readonly IReadOnlyDictionary<byte, HudOption>   ByKey;

    public static readonly int[] DefaultSettings;

    static HudOptions()
    {
        for (var i = 0; i < All.Length; i++)
        {
            All[i].Index = i;
        }

        ById            = All.ToDictionary(o => o.Id);
        ByKey           = All.ToDictionary(o => o.Key);
        DefaultSettings = All.Select(o => o.Initial).ToArray();
    }

    public static int[] NewSettings()
        => (int[]) DefaultSettings.Clone();

    /// <summary>
    ///     What an option's pill shows: its choice, or Off and greyed out while the option doesn't apply.
    /// </summary>
    public static (HudChoice Choice, bool Disabled) Display(HudOption option, int[] settings)
        => option.Needs is { } needs && !needs(settings)
            ? (Disabled, true)
            : (option.Choices[settings[option.Index]], false);

    public static bool IsOn(int[] settings, HudOption option)
        => option.Choices[settings[option.Index]].Label == "On";
}

/// <summary>
///     The timer's reorderable lines, below its fixed heading (title, time, stage). Each shows in the
///     Line&lt;i&gt; slot matching its place in the player's order, and row &lt;i&gt; of the Timer tab edits it.
/// </summary>
/// <summary>
///     The timer's lines. Their values are saved (PlayerSettingsCodec): only ever add at the end.
/// </summary>
internal enum HudLine
{
    Zone,
    Gap,
    Mode,
    Speed,
    Start,
    Sync,
    Jumps,
    Strafes,
}

internal static class HudLines
{
    /// <summary>
    ///     The layout's slot classes match this order.
    /// </summary>
    public static readonly HudLine[] DefaultOrder = Enum.GetValues<HudLine>();

    public static readonly int Count = DefaultOrder.Length;

    public static readonly string[] SlotIds   = Ids("Line{0}");
    public static readonly string[] RowNames  = Ids("Row{0}Name");
    public static readonly string[] RowValues = Ids("Row{0}Value");
    public static readonly string[] RowToggle = Ids("Row{0}Toggle");

    public static HudText Name(HudLine line)
        => line switch
        {
            HudLine.Zone    => HudTexts.RowZone,
            HudLine.Gap     => HudTexts.RowBlank,
            HudLine.Mode    => HudTexts.RowMode,
            HudLine.Speed   => HudTexts.RowSpeed,
            HudLine.Start   => HudTexts.RowStart,
            HudLine.Sync    => HudTexts.RowSync,
            HudLine.Jumps   => HudTexts.RowJumps,
            HudLine.Strafes => HudTexts.RowStrafes,
            _               => throw new ArgumentOutOfRangeException(nameof(line), line, null),
        };

    /// <summary>
    ///     The line's on/off setting; the blank line has none.
    /// </summary>
    public static HudOption? Option(HudLine line)
        => line switch
        {
            HudLine.Zone    => HudOptions.Zone,
            HudLine.Mode    => HudOptions.Mode,
            HudLine.Speed   => HudOptions.Speed,
            HudLine.Start   => HudOptions.Start,
            HudLine.Sync    => HudOptions.Sync,
            HudLine.Jumps   => HudOptions.Jumps,
            HudLine.Strafes => HudOptions.Strafes,
            _               => null,
        };

    /// <summary>
    ///     Options switched from the Timer tab's rows rather than a button of their own.
    /// </summary>
    public static readonly HashSet<HudOption> RowOptions =
        DefaultOrder.Select(Option).OfType<HudOption>().ToHashSet();

    /// <summary>
    ///     A saved order, or null unless it holds every line exactly once.
    /// </summary>
    public static HudLine[]? Parse(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count != Count)
        {
            return null;
        }

        var order = new HudLine[Count];

        for (var i = 0; i < Count; i++)
        {
            if (!Enum.TryParse(names[i], out order[i]) || !Enum.IsDefined(order[i]))
            {
                return null;
            }
        }

        return order.Distinct().Count() == Count ? order : null;
    }

    private static string[] Ids(string format)
        => Enumerable.Range(0, DefaultOrder.Length).Select(i => string.Format(format, i)).ToArray();
}

/// <summary>
///     Panels a player can drag. Saved by <see cref="HudTargetDef.Key" />, not by this value.
/// </summary>
internal enum HudTarget
{
    Menu,
    Run,
    CSpeed,
    Info,
    Splits,
    Keys,
    Locs,
    Ssj,
    Spec,
}

/// <summary>
///     A dragged panel is centre-aligned and positioned by its centre's offset from the screen centre, in whole
///     percents (-50..50): tens go on <see cref="Panel" /> as mx-/my- classes, units on its full-screen
///     <see cref="Wrapper" /> as fx-/fy- (hud_positions.css). Being centre-based, the centre line is offset 0,
///     so snapping to it never changes alignment and can glide. A panel keeps its CSS position until first
///     dragged and goes back to it on reset; <see cref="Start" /> is roughly where the CSS puts its centre at 16:9,
///     where a drag starts. <see cref="Size" /> is its rough size in percent. Panels are kept fully on screen by
///     their size, except <see cref="Offscreen" /> ones, which may go until their centre reaches the edge.
/// </summary>
internal sealed record HudTargetDef(
    string          Panel,
    string          Wrapper,
    (float X, float Y) Start,
    (float W, float H) Size,
    bool            Offscreen,
    HudText         Name,
    byte            Key); // saved; never reused

internal static class HudTargets
{
    public static readonly HudTarget[] All = Enum.GetValues<HudTarget>();

    public static readonly int Count = All.Length;

    public static readonly IReadOnlyDictionary<byte, HudTarget> ByKey;

    private static readonly HudTargetDef[] Defs =
    [
        new ("Menu", "MenuPos", (-39, 0), (20, 58), false, HudTexts.TargetMenu, 1),
        new ("RunPanel", "RunPos", (0, 28), (15, 18), true, HudTexts.TargetRun, 2),
        new ("CSpeedPanel", "CSpeedPos", (0, 10), (6, 5), true, HudTexts.TargetCSpeed, 3),
        new ("InfoPanel", "InfoPos", (-46, -47), (10, 6), true, HudTexts.TargetInfo, 4),
        new ("SplitsPanel", "SplitsPos", (-40, -34), (20, 19), true, HudTexts.TargetSplits, 5),
        new ("KeysPanel", "KeysPos", (0, -20), (11, 13), true, HudTexts.TargetKeys, 6),
        new ("LocsPanel", "LocsPos", (-42, 0), (13, 21), true, HudTexts.TargetLocs, 7),
        new ("SsjPanel", "SsjPos", (0, 5), (22, 4), true, HudTexts.TargetSsj, 8),
        new ("SpecPanel", "SpecPos", (44, 0), (11, 18), true, HudTexts.TargetSpecs, 9),
    ];

    /// <summary>
    ///     Clicking one of these while the menu is open picks up its panel.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, HudTarget> Buttons = new Dictionary<string, HudTarget>
    {
        ["MenuMove"]    = HudTarget.Menu,
        ["RunPanel"]    = HudTarget.Run,
        ["CSpeedPanel"] = HudTarget.CSpeed,
        ["InfoPanel"]   = HudTarget.Info,
        ["SplitsPanel"] = HudTarget.Splits,
        ["KeysPanel"]   = HudTarget.Keys,
        ["LocsPanel"]   = HudTarget.Locs,
        ["SsjPanel"]    = HudTarget.Ssj,
        ["SpecPanel"]   = HudTarget.Spec,
    };

    static HudTargets()
        => ByKey = All.ToDictionary(t => Defs[(int) t].Key);

    public static HudTargetDef Def(HudTarget target)
        => Defs[(int) target];
}

/// <summary>
///     Menu tabs: button Tab&lt;name&gt;, its label Tab&lt;name&gt;Label, page Page&lt;name&gt;. The layout starts on
///     the first.
/// </summary>
internal static class HudTabs
{
    public static readonly string[] Names  = ["Hud", "Timer", "Speed", "Splits", "Keys", "Ssj"];
    public static readonly string[] Tabs   = Names.Select(n => $"Tab{n}").ToArray();
    public static readonly string[] Labels = Names.Select(n => $"Tab{n}Label").ToArray();
    public static readonly string[] Pages  = Names.Select(n => $"Page{n}").ToArray();

    public static readonly int Ssj = Array.IndexOf(Names, "Ssj");
}
