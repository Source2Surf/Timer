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
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Utilities;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

internal partial class RecordModule
{
    private (int style, int track) GetStyleTrack(PlayerSlot slot)
    {
        var timerInfo = _timerModule.GetTimerInfo(slot);

        return (timerInfo?.Style ?? 0, timerInfo?.Track ?? 0);
    }

    private ECommandAction OnCommandStageWR(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr             = _localization.For(slot);
        var (style, track) = GetStyleTrack(slot);

        var stage = command.TryGetArg<byte>(1, out var s) ? s : 0;

        if (stage < 1)
        {
            controller.PrintToChat(tr[ChatTexts.UsageSwr]);
            return ECommandAction.Handled;
        }

        var wr = _mapCache.GetWR(style, track, stage);

        if (wr is null)
        {
            controller.PrintToChat(tr.Format(ChatTexts.SrStageNone, stage));
            return ECommandAction.Handled;
        }

        controller.PrintToChat(tr.Format(ChatTexts.SrStage, stage, Utils.ColoredTime(wr.Time), Utils.Highlight(wr.PlayerName)));

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandBonusTop(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr         = _localization.For(slot);
        var (style, _) = GetStyleTrack(slot);

        var bonus = command.TryGetArg<byte>(1, out var b) ? b : 1;

        if (bonus < 1)
        {
            controller.PrintToChat(tr[ChatTexts.UsageBtop]);
            return ECommandAction.Handled;
        }

        var records = _mapCache.GetRecords(style, bonus);

        if (records.Count == 0)
        {
            controller.PrintToChat(tr.Format(ChatTexts.TopBonusNone, bonus));
            return ECommandAction.Handled;
        }

        controller.PrintToChat(tr.Format(ChatTexts.TopBonus, bonus));

        var count = Math.Min(records.Count, 10);

        for (var i = 0; i < count; i++)
        {
            var rec = records[i];

            controller.PrintToChat(tr.Format(ChatTexts.TopRow, i + 1, Utils.ColoredTime(rec.Time), Utils.Highlight(rec.PlayerName)));
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandBonusWR(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr         = _localization.For(slot);
        var (style, _) = GetStyleTrack(slot);

        var bonus = command.TryGetArg<byte>(1, out var b) ? b : 1;

        if (bonus < 1)
        {
            controller.PrintToChat(tr[ChatTexts.UsageBwr]);
            return ECommandAction.Handled;
        }

        var wr = GetWR(style, bonus);

        if (wr is null)
        {
            controller.PrintToChat(tr.Format(ChatTexts.SrBonusNone, bonus));
            return ECommandAction.Handled;
        }

        controller.PrintToChat(tr.Format(ChatTexts.SrBonus, bonus, Utils.ColoredTime(wr.Time), Utils.Highlight(wr.PlayerName)));

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandBonusPB(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr         = _localization.For(slot);
        var (style, _) = GetStyleTrack(slot);

        var bonus = command.TryGetArg<byte>(1, out var b) ? b : 1;

        if (bonus < 1 || bonus >= TimerConstants.MAX_TRACK)
        {
            controller.PrintToChat(tr[ChatTexts.UsageBpb]);
            return ECommandAction.Handled;
        }

        var pb = GetPlayerRecord(slot, style, bonus);

        if (pb is null)
        {
            controller.PrintToChat(tr.Format(ChatTexts.PbBonusNone, bonus));
            return ECommandAction.Handled;
        }

        var records = _mapCache.GetRecords(style, bonus);
        var rank    = _mapCache.GetRankOfRecord(style, bonus, pb);

        controller.PrintToChat(tr.Format(ChatTexts.PbBonus, bonus, Utils.ColoredTime(pb.Time), rank, records.Count));

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandStagePB(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr             = _localization.For(slot);
        var (style, track) = GetStyleTrack(slot);

        var stage = command.TryGetArg<byte>(1, out var s) ? s : 0;

        if (stage < 1)
        {
            controller.PrintToChat(tr[ChatTexts.UsageSpb]);
            return ECommandAction.Handled;
        }

        var pb = GetPlayerRecord(slot, style, track, stage);

        if (pb is null)
        {
            controller.PrintToChat(tr.Format(ChatTexts.PbStageNone, stage));
            return ECommandAction.Handled;
        }

        var wr      = _mapCache.GetWR(style, track, stage);
        var message = tr.Format(ChatTexts.PbStage, stage, Utils.ColoredTime(pb.Time));

        if (wr is not null)
        {
            message += tr.Format(ChatTexts.PbVsSr, Utils.SignedDelta(pb.Time - wr.Time));
        }

        controller.PrintToChat(message);

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandClearRecords(StringCommand stringCommand)
    {
        _request.RemoveMapRecords(_bridge.CurrentMapName);

        _mapCache.Clear();
        _playerCache.ClearAll();

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandWR(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        if (LeaderboardRequested is { } open)
        {
            var map = command.ArgString.Trim();

            if (map.Length == 0)
            {
                open(slot, null);
            }
            else
            {
                OpenLeaderboard(slot, map);
            }

            return ECommandAction.Handled;
        }

        var tr             = _localization.For(slot);
        var (style, track) = GetStyleTrack(slot);

        var wr = GetWR(style, track);

        controller.PrintToChat(wr is null ? tr[ChatTexts.SrNone] : tr.Format(ChatTexts.Sr, Utils.ColoredTime(wr.Time)));

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandPB(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr             = _localization.For(slot);
        var (style, track) = GetStyleTrack(slot);

        var pb = GetPlayerRecord(slot, style, track);

        if (pb is null)
        {
            controller.PrintToChat(tr[ChatTexts.PbNone]);
            return ECommandAction.Handled;
        }

        var rank  = GetRankOfRecord(style, track, pb);
        var total = GetTotalRecordCount(style, track);

        controller.PrintToChat(tr.Format(ChatTexts.Pb, Utils.ColoredTime(pb.Time), rank, total));

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandRank(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr             = _localization.For(slot);
        var (style, track) = GetStyleTrack(slot);

        var pb = GetPlayerRecord(slot, style, track);

        if (pb is null)
        {
            controller.PrintToChat(tr[ChatTexts.RankNone]);
            return ECommandAction.Handled;
        }

        var rank  = GetRankOfRecord(style, track, pb);
        var total = GetTotalRecordCount(style, track);

        controller.PrintToChat(tr.Format(ChatTexts.Rank, Utils.Highlight(rank), total, Utils.ColoredTime(pb.Time)));

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandTop(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr             = _localization.For(slot);
        var (style, track) = GetStyleTrack(slot);

        var wr = GetWR(style, track);

        if (wr is null)
        {
            controller.PrintToChat(tr[ChatTexts.TopNone]);
            return ECommandAction.Handled;
        }

        var total = GetTotalRecordCount(style, track);

        controller.PrintToChat(tr.Format(ChatTexts.Top, Utils.ColoredTime(wr.Time), total));

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandCpr(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr             = _localization.For(slot);
        var (style, track) = GetStyleTrack(slot);

        var pb = GetPlayerRecord(slot, style, track);

        if (pb is null)
        {
            controller.PrintToChat(tr[ChatTexts.PbNone]);
            return ECommandAction.Handled;
        }

        var wrCheckpoints = _mapCache.GetWRCheckpoints(style, track);

        if (wrCheckpoints is not { Count: > 0 })
        {
            controller.PrintToChat(tr[ChatTexts.CprNoSr]);
            return ECommandAction.Handled;
        }

        AsyncChatCommand.Run(_bridge, _logger, slot, "GetRecordCheckpoints",
                             () => _request.GetRecordCheckpoints(pb.Id),
                             (ctrl, pbCheckpoints) =>
                             {
                                 if (pbCheckpoints.Count == 0)
                                 {
                                     ctrl.PrintToChat(tr[ChatTexts.CprNoPb]);

                                     return;
                                 }

                                 var count = Math.Min(pbCheckpoints.Count, wrCheckpoints.Count);

                                 ctrl.PrintToChat(tr[ChatTexts.CprTitle]);

                                 for (var i = 0; i < count; i++)
                                 {
                                     var pbCp = pbCheckpoints[i];
                                     var wrCp = wrCheckpoints[i];

                                     ctrl.PrintToChat(tr.Format(ChatTexts.Checkpoint, i + 1, Utils.ColoredTime(pbCp.Time))
                                                      + tr.Format(ChatTexts.VsSr, Utils.SignedDelta(pbCp.Time - wrCp.Time)));
                                 }

                                 // Final time diff
                                 if (GetWR(style, track) is not { } wr)
                                 {
                                     return;
                                 }

                                 ctrl.PrintToChat(tr.Format(ChatTexts.CprFinal, Utils.ColoredTime(pb.Time))
                                                  + tr.Format(ChatTexts.VsSr, Utils.SignedDelta(pb.Time - wr.Time)));
                             });

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandRecent(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetClientController(slot, out var client, out _))
        {
            return ECommandAction.Handled;
        }

        var tr      = _localization.For(slot);
        var mapName = _bridge.CurrentMapName;
        var steamId = client.SteamId;

        AsyncChatCommand.Run(_bridge, _logger, slot, "GetRecentRecords",
                             () => _request.GetRecentRecords(mapName, steamId),
                             (ctrl, records) =>
                             {
                                 if (records.Count == 0)
                                 {
                                     ctrl.PrintToChat(tr[ChatTexts.RecentNone]);

                                     return;
                                 }

                                 ctrl.PrintToChat(tr[ChatTexts.RecentTitle]);

                                 foreach (var record in records)
                                 {
                                     var time = Utils.ColoredTime(record.Time);
                                     var date = string.Concat(ChatColor.Grey, record.RunDate.ToString("MM-dd HH:mm"), ChatColor.White);

                                     ctrl.PrintToChat(record.Track > 0
                                                          ? tr.Format(ChatTexts.RecentRowBonus, time, record.Track, date)
                                                          : tr.Format(ChatTexts.RecentRow, time, date));
                                 }
                             });

        return ECommandAction.Handled;
    }
}
