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

using System.Globalization;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Shared;

namespace Source2Surf.Timer.Modules;

// SSJ: every real player's jumps are tracked (spectators see them); the panel shows the jumps its viewer picked.
internal partial class HudModule
{
    private const float DefaultAirMaxWish = 30f;
    private const float SsjHoldTime       = 3f;   // seconds a jump stays up before it fades out
    private const float SsjFadeTime       = 0.4f; // hud.css .SsjCard transition

    // bhop-get-stats' tiers: where bad, meh, good and really good start; below the first is really bad.
    private static readonly float[] PercentBars = [0.60f, 0.70f, 0.80f, 0.90f];

    // Its default colour for each tier, by SsjTier; "plain" for values it doesn't rate.
    private static readonly string[] TierNames = ["red", "orange", "green", "cyan", "white"];

    private static readonly string[] SsjRows   = SsjIds("SsjRow{0}");
    private static readonly string[] SsjNames  = SsjIds("SsjRow{0}Name");
    private static readonly string[] SsjValues = SsjIds("SsjRow{0}Value");

    private void OnSsjMovePre(IPlayerProcessMoveForwardParams param)
    {
        if (_players[param.Client.Slot] is not { } p)
        {
            return;
        }

        var pawn = param.Pawn;

        if (!pawn.IsAlive || pawn.ActualMoveType != MoveType.Walk)
        {
            p.Ssj.Break();

            return;
        }

        var velocity = param.Velocity;
        p.Ssj.BeginStep(velocity.X, velocity.Y, pawn.GroundEntityHandle.IsValid());
    }

    // CheckParameters has scaled the moves to units by now, as AirAccelerate gets them.
    private unsafe void OnSsjMovePost(IPlayerProcessMoveForwardParams param)
    {
        if (_players[param.Client.Slot] is not { Ssj.InStep: true } p)
        {
            return;
        }

        var mv = param.Info;

        p.Ssj.EndStep(_bridge.GlobalVars.FrameTime / TimerConstants.TickInterval,
                      mv->ViewAngles.Y,
                      mv->ForwardMove,
                      mv->SideMove,
                      _airMaxWish,
                      mv->AbsOrigin.Z);
    }

    private void OnAirMaxWishChanged(IConVar conVar)
        => _airMaxWish = conVar.GetFloat();

    // Fired from the jump inside the step, so the step that ends next is the takeoff.
    private void OnSsjJump(IGameEvent e)
    {
        if (e.GetPlayerController("userid") is { IsValidEntity: true } controller && _players[controller.PlayerSlot] is { } p)
        {
            p.Ssj.Jumped();
        }
    }

    // Opens the menu on the SSJ tab, or closes it there.
    private ECommandAction OnCommandSsj(PlayerSlot slot, StringCommand command)
    {
        if (_players[slot] is { } p)
        {
            var close = p.MenuOpen && p.Tab == HudTabs.Ssj;
            p.Tab = HudTabs.Ssj;
            SetMenuOpen(p, !close);
            RefreshNow(p);
        }

        return ECommandAction.Handled;
    }

    /// <summary>
    ///     The latest jump of the shown player that matches the viewer's pick, on one line, for a few seconds (all
    ///     the while the menu is open, to drag it). A replay bot's movement isn't played through, so it has none.
    /// </summary>
    private static void UpdateSsj(HudWriter w, HudPlayer p, HudSource s, float now)
    {
        var tracker = s.Replay is null ? s.Run?.Ssj : null;
        var jump    = tracker is null ? null : PickSsj(p, tracker, now);
        var tr      = p.Tr;

        w.Class("SsjCard", "Hidden", jump is null);
        w.Class("SsjEmpty", "shown", p.MenuOpen && jump is null);

        if (p.MenuOpen)
        {
            w.Text("SsjEmpty", "text", tr[HudTexts.SsjEmpty]);
        }

        if (jump is null)
        {
            return;
        }

        var (gone, snap) = SsjFade(now - p.SsjShownAt, p.MenuOpen, p.SsjSnap, p.SsjWasGone);

        p.SsjSnap    = snap;
        p.SsjWasGone = gone;
        w.Class("SsjCard", "gone", gone);
        w.Class("SsjCard", "snap", snap);
        w.Text("SsjJump", "text", tr.Format(HudTexts.SsjJumpN, jump.Number));
        w.Text("SsjSpeed", "text", HudFormat.RoundSpeed(jump.Speed).ToString(CultureInfo.InvariantCulture));

        var row = 0;

        if (jump.Stats is { } stats)
        {
            if (p.IsOn(HudOptions.SsjSpeedDiff))
            {
                SsjRow(w, tr, row++, null, HudFormat.Signed(stats.SpeedDiff), null);
            }

            if (p.IsOn(HudOptions.SsjHeight))
            {
                SsjRow(w, tr, row++, HudTexts.SsjHeightN, HudFormat.Signed(stats.HeightDiff), null);
            }

            if (p.IsOn(HudOptions.SsjGain))
            {
                SsjRow(w, tr, row++, HudTexts.SsjGainN, HudFormat.Percent(stats.Gain), HudFormat.Tier(stats.Gain, PercentBars));
            }

            if (p.IsOn(HudOptions.SsjSync))
            {
                SsjRow(w, tr, row++, HudTexts.SsjSyncN, HudFormat.Percent(stats.Sync), HudFormat.Tier(stats.Sync, PercentBars));
            }

            if (p.IsOn(HudOptions.SsjStrafes))
            {
                SsjRow(w, tr, row++, HudTexts.SsjStrafesN, stats.Strafes.ToString(CultureInfo.InvariantCulture), null);
            }

            if (p.IsOn(HudOptions.SsjEfficiency))
            {
                SsjRow(w, tr, row++, HudTexts.SsjEfficiencyN, HudFormat.Percent(stats.Efficiency), HudFormat.Tier(stats.Efficiency, PercentBars));
            }
        }

        for (var i = 0; i < SsjRows.Length; i++)
        {
            w.Class(SsjRows[i], "Hidden", i >= row);
        }
    }

    // The speed change goes unnamed, next to the speed.
    private static void SsjRow(HudWriter w, HudTr tr, int i, HudText? name, string value, SsjTier? tier)
    {
        w.Class(SsjNames[i], "Hidden", name is null);

        if (name is not null)
        {
            w.Text(SsjNames[i], "text", tr[name]);
        }

        w.Text(SsjValues[i], "text", value);
        w.Numbered(SsjValues[i], "tier", TierName(tier));
    }

    /// <summary>
    ///     Catches the viewer up on the shown player's jumps since the last refresh, keeping the newest they picked.
    ///     Switching to another player starts from their next jump.
    /// </summary>
    private static SsjJump? PickSsj(HudPlayer p, SsjTracker tracker, float now)
    {
        if (!ReferenceEquals(p.SsjFrom, tracker))
        {
            p.SsjFrom  = tracker;
            p.SsjSeen  = tracker.Serial;
            p.SsjShown = null;
        }

        var every  = p.Settings[HudOptions.SsjJump.Index] + 1;
        var repeat = p.IsOn(HudOptions.SsjRepeat);
        var first  = p.IsOn(HudOptions.SsjFirst);

        while (tracker.After(p.SsjSeen) is { } jump)
        {
            p.SsjSeen = jump.Serial;

            if (SsjTracker.Shows(jump.Number, every, repeat, first))
            {
                p.SsjShown   = jump;
                p.SsjShownAt = now;
            }
        }

        p.SsjSeen = tracker.Serial;

        return p.SsjShown;
    }

    /// <summary>
    ///     Gone a few seconds after the jump showed (all the while the menu is open it stays). Once faded out it snaps
    ///     (no transition) and stays snapped for the update it comes back in, so it shows at once and only going
    ///     fades. CurTime starts over on a map change, so a time ahead of it is stale.
    /// </summary>
    internal static (bool Gone, bool Snap) SsjFade(float shownFor, bool menuOpen, bool wasSnapped, bool wasGone)
    {
        var gone = !menuOpen && (shownFor > SsjHoldTime || shownFor < 0);
        var snap = gone ? shownFor > SsjHoldTime + SsjFadeTime || shownFor < 0 : wasSnapped && wasGone;

        return (gone, snap);
    }

    private static string TierName(SsjTier? tier)
        => tier is { } t ? TierNames[(int) t] : "plain";

    private static string[] SsjIds(string format)
    {
        var ids = new string[6]; // one per stat

        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = string.Format(CultureInfo.InvariantCulture, format, i);
        }

        return ids;
    }
}
