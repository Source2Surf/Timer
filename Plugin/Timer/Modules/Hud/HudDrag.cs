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
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Modules.Hud;

namespace Source2Surf.Timer.Modules;

// Dragging panels, like cs2kz's HUD edit mode: clicking a panel (or the menu header) while the menu is open leaves
// cursor mode, and from then on the player's view-angle changes (their mouse) move it until they place it. A camera
// holds what they see still meanwhile, and their view is handed back afterwards.
internal partial class HudModule
{
    private const float ReferenceWidth   = 1920f; // layout units; the layout is 1080 tall and assumed 16:9 for drag speed
    private const float ReferenceHeight  = 1080f;
    private const float DragPxPerDegree  = 12f;   // how far a panel moves per degree the player turns
    private const float DragArmDelay     = 0.15f; // the click that picked a panel up can't also place it
    private const float DragLevelPitch   = 45f;   // steeper than this at pickup, the view is levelled first
    private const float DragSnapLanded   = 5f;    // pitch within this of level means the levelling reached the client
    private const float DragSnapTimeout  = 1f;
    private const float DragSendInterval = 0.1f;  // position updates at most this often; the CSS glides in between
    private const float SmoothTail       = 0.25f; // keep the glide on this long after placing, for the last step

    private static readonly Dictionary<string, KeyValuesVariantValueItem> NoKeyValues = [];

    private static readonly CEntityHandle<IBaseEntity> NoEntity = new (uint.MaxValue); // default(...) is the world

    private enum DragEnd
    {
        Place,
        Cancel,
        Default,
    }

    /// <summary>
    ///     Keeps the panel on screen going by its rough size; an offscreen panel only keeps its centre on screen.
    ///     The menu never goes off, so its buttons stay clickable.
    /// </summary>
    private static void ClampPosition(HudPlayer p, HudTarget target)
    {
        if (p.Positions[(int) target] is not { } pos)
        {
            return;
        }

        var def = HudTargets.Def(target);
        pos.X = HudFormat.ClampAbs(pos.X, def.Offscreen ? 50 : Math.Max(0, 50 - (def.Size.W / 2)));
        pos.Y = HudFormat.ClampAbs(pos.Y, def.Offscreen ? 50 : Math.Max(0, 50 - (def.Size.H / 2)));
    }

    private void StartDrag(HudPlayer p, IBasePlayerPawn pawn, HudTarget target)
    {
        var view = pawn.GetEyeAngles();
        var now  = _bridge.GlobalVars.CurTime;
        var t    = (int) target;

        // A panel's whole vertical range is well under 90 degrees of pitch, so only a steep view needs levelling.
        var level = MathF.Abs(view.X) > DragLevelPitch;

        if (level)
        {
            pawn.SnapViewAngles(new Vector(0, view.Y, 0));
        }

        if (!float.IsNaN(p.UnplaceAt[t]))
        {
            p.UnplaceAt[t] = float.NaN;
            p.Positions[t] = null;
        }

        var before = p.Positions[t];
        var start  = HudTargets.Def(target).Start;
        p.Positions[t] = before?.Copy() ?? new HudPosition { X = start.X, Y = start.Y };

        p.Drag = new HudDragState
        {
            Target     = target,
            Before     = before,
            View       = view,
            LastYaw    = view.Y,
            LastPitch  = level ? 0 : view.X,
            ArmedAt    = now + DragArmDelay,
            NextSendAt = now,
            SnapSince  = level ? now : null,
            Aim        = view,
        };

        FreezeView(p, p.Drag, pawn, view);
        p.MenuDirty = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, false);
    }

    private void EndDrag(HudPlayer p, DragEnd how)
    {
        if (p.Drag is not { } drag)
        {
            return;
        }

        p.Drag = null;

        var now = _bridge.GlobalVars.CurTime;

        // Keep rebuilding the menu until the panel has finished gliding or jumped back to its CSS layout.
        p.MenuDirty     = true;
        p.MenuBusyUntil = now + SmoothTail + (2 * DragSendInterval);
        var t   = (int) drag.Target;
        var final = how switch
        {
            DragEnd.Place  => p.Positions[t],
            DragEnd.Cancel => drag.Before,
            _              => null,
        };

        if (final is not null)
        {
            p.Positions[t] = final;

            // Keep gliding for the last step.
            if (drag.Smooth)
            {
                p.SmoothUntil[t] = now + SmoothTail;
            }
        }
        else if (drag.Smooth)
        {
            // Back to the CSS layout changes alignment: that must jump, not glide. The glide goes off now and the
            // switch follows one update later, so the two can't land in the same frame.
            p.UnplaceAt[t] = now + DragSendInterval;
        }
        else
        {
            p.Positions[t] = null;
        }

        if (how != DragEnd.Cancel)
        {
            MarkSettingsChanged(p);
        }

        // Hand back the view the drag froze and turned.
        UnfreezeView(p, drag);

        // Teleport would also tilt the pawn by the pitch, which carries its eyes forward.
        if (_bridge.TryGetController(p.Slot, out var controller) && controller.GetPlayerPawn() is { IsAlive: true } pawn)
        {
            pawn.SnapViewAngles(drag.View);
        }

        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
        RefreshNow(p); // show where it landed without waiting for the refresh
    }

    private void TickDrag(HudPlayer             p,
                          HudDragState          drag,
                          UserCommandButtons    buttons,
                          UserCommandButtons    changed,
                          float                 now)
    {
        // Keys only count after a moment, so the click that picked the panel up can't also place it.
        if (now >= drag.ArmedAt)
        {
            if (JustPressed(UserCommandButtons.Attack))
            {
                EndDrag(p, DragEnd.Place);

                return;
            }

            if (JustPressed(UserCommandButtons.Attack2))
            {
                EndDrag(p, DragEnd.Cancel);

                return;
            }

            if (JustPressed(UserCommandButtons.Reload))
            {
                EndDrag(p, DragEnd.Default);

                return;
            }
        }

        var eye = drag.Aim;
        var yaw = AngleDelta(eye.Y, drag.LastYaw);
        drag.LastYaw = eye.Y;

        var pitch = 0f;

        if (drag.SnapSince is { } since)
        {
            // Until the client applies the levelling its angles carry on from the old ones; wait for it to land.
            if (MathF.Abs(eye.X) < DragSnapLanded || now > since + DragSnapTimeout || now < since)
            {
                drag.SnapSince = null;
                drag.LastPitch = eye.X;
            }
        }
        else
        {
            pitch          = eye.X - drag.LastPitch;
            drag.LastPitch = eye.X;
        }

        // Turning right lowers yaw, looking down raises pitch. Offsets are percents of the screen. X / Y keep
        // following the mouse while snapped, so moving away lets go naturally.
        var pos = p.Positions[(int) drag.Target]!;
        pos.X -= yaw * DragPxPerDegree / ReferenceWidth * 100f;
        pos.Y += pitch * DragPxPerDegree / ReferenceHeight * 100f;
        ClampPosition(p, drag.Target);

        // Holding walk places freely, without snapping.
        var free = (buttons & UserCommandButtons.Speed) != 0;
        pos.Cx = !free && HudFormat.SnapsToCentre(pos.X, pos.Cx);
        pos.Cy = !free && HudFormat.SnapsToCentre(pos.Y, pos.Cy);

        if (now < drag.NextSendAt && drag.NextSendAt - now <= DragSendInterval)
        {
            return;
        }

        if (GetLayout(p) is not { } layout)
        {
            return;
        }

        drag.NextSendAt = now + DragSendInterval;

        // Glide between updates, snaps included, except on the first one: that's where the panel leaves its CSS
        // layout for centre alignment, which has to jump.
        drag.Smooth = drag.Sends > 0;
        drag.Sends++;

        var w = new HudWriter(layout, p);
        SetSmooth(w, drag.Target, drag.Smooth);
        ApplyPosition(w, p, drag.Target);
        w.Class("GuideX", "shown", pos.Cx);
        w.Class("GuideY", "shown", pos.Cy);

        return;

        bool JustPressed(UserCommandButtons button)
            => (buttons & button) != 0 && (changed & button) != 0;
    }

    /// <summary>
    ///     Holds what the player sees still while they drag: a controlled camera at their eyes, facing where they
    ///     faced, as their view entity. Their mouse still turns the view their client sends, which moves the panel.
    /// </summary>
    private void FreezeView(HudPlayer p, HudDragState drag, IBasePlayerPawn pawn, Vector view)
    {
        if (pawn.GetCameraService() is not { } cameras)
        {
            return;
        }

        var camera = p.Camera;

        if (camera is null || !camera.IsValid())
        {
            camera = _bridge.EntityManager.SpawnEntitySync<ICustomPlayerCamera>("custom_player_camera", NoKeyValues);

            if (camera is not { IsValidEntity: true })
            {
                p.Camera = null;

                return; // the drag still works, the view just turns with it
            }

            p.Camera = camera;
        }

        camera.Teleport(pawn.GetEyePosition(), view);
        camera.PawnHandle = pawn.RefHandle.As<IBasePlayerPawn>();
        camera.CameraMode = CustomCameraMode.Controlled;

        drag.Frozen        = true;
        drag.Pawn          = pawn.RefHandle.As<IBasePlayerPawn>();
        drag.PreviousView  = HandsBack(cameras.ViewEntityHandle, pawn, camera) ? cameras.ViewEntityHandle : NoEntity;
        cameras.ViewEntity = camera;
    }

    /// <summary>
    ///     Gives the view back to the map camera that held it before the drag, else to the player's own eyes, and
    ///     switches the camera off. The pawn is found by its own handle, and a view entity that's gone is let go of
    ///     too, so a camera something else removed can't keep holding the view either.
    /// </summary>
    private void UnfreezeView(HudPlayer p, HudDragState drag)
    {
        if (!drag.Frozen)
        {
            return;
        }

        drag.Frozen = false;

        var camera = p.Camera is { } c && c.IsValid() ? c : null;

        if (_bridge.EntityManager.FindEntityByHandle(drag.Pawn) is { IsValidEntity: true } pawn
            && pawn.GetCameraService() is { } cameras)
        {
            var current = cameras.ViewEntityHandle;
            var held    = camera is not null && current == camera.Handle;
            var gone    = current.IsValid() && _bridge.EntityManager.FindEntityByHandle(current) is not { IsValidEntity: true };

            if (held || gone)
            {
                cameras.ViewEntityHandle = HandsBack(drag.PreviousView, pawn, camera) ? drag.PreviousView : NoEntity;
            }
        }

        if (camera is not null)
        {
            camera.CameraMode = CustomCameraMode.Disabled;
        }
    }

    /// <summary>
    ///     Whether a view entity is worth handing back: one that still exists and is neither the player nor the
    ///     drag camera.
    /// </summary>
    private bool HandsBack(CEntityHandle<IBaseEntity> view, IBasePlayerPawn pawn, ICustomPlayerCamera? camera)
        => view.IsValid()
           && _bridge.EntityManager.FindEntityByHandle(view) is { IsValidEntity: true } entity
           && entity.Index != pawn.Index
           && (camera is null || entity.Index != camera.Index);

    /// <summary>
    ///     The glide lives on both the panel and its wrapper, since each carries part of the offset.
    /// </summary>
    private static void SetSmooth(HudWriter w, HudTarget target, bool on)
    {
        var def = HudTargets.Def(target);
        w.Class(def.Panel, "smooth", on);
        w.Class(def.Wrapper, "smooth", on);
    }

    /// <summary>
    ///     Tens of percent on the panel plus units on its full-screen wrapper (see <see cref="HudFormat.SplitOffset" />).
    ///     A panel without a position gets none of these classes, so its CSS layout applies.
    /// </summary>
    private static void ApplyPosition(HudWriter w, HudPlayer p, HudTarget target)
    {
        var def = HudTargets.Def(target);

        if (p.Positions[(int) target] is not { } pos)
        {
            w.Numbered(def.Panel, "mx", null);
            w.Numbered(def.Panel, "my", null);
            w.Numbered(def.Wrapper, "fx", null);
            w.Numbered(def.Wrapper, "fy", null);

            return;
        }

        var (tensX, unitsX) = HudFormat.SplitOffset(HudFormat.ShownOffset(pos.X, pos.Cx));
        var (tensY, unitsY) = HudFormat.SplitOffset(HudFormat.ShownOffset(pos.Y, pos.Cy));

        w.Numbered(def.Panel, "mx", tensX);
        w.Numbered(def.Panel, "my", tensY);
        w.Numbered(def.Wrapper, "fx", unitsX);
        w.Numbered(def.Wrapper, "fy", unitsY);
    }
}
