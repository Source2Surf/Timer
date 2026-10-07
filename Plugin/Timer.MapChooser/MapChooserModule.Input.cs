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

using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;

namespace Timer.MapChooser;

// A vote is cast without leaving the game: the vote keys move along its options and select votes, as in
// MenuManager, or the option's number goes in chat. Walk + select stays the timer's own.
public sealed partial class MapChooserModule
{
    // The timer's chat command prefixes, so "!3" votes like "3".
    private const string ChatTriggers = "!/.！．／。";

    private ECommandAction OnKeyUp(IGameClient client, StringCommand command)
        => MoveCursor(client, -1);

    private ECommandAction OnKeyDown(IGameClient client, StringCommand command)
        => MoveCursor(client, 1);

    private ECommandAction MoveCursor(IGameClient client, int step)
    {
        if (_vote is not { } vote || client.IsFakeClient)
        {
            return ECommandAction.Skipped;
        }

        var slot  = (int) client.Slot;
        var count = vote.Options.Count;

        _cursor[slot] = (((_cursor[slot] + step) % count) + count) % count;
        Changed(slot);

        return ECommandAction.Handled;
    }

    private void OnPlayerRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> ret)
    {
        if (_vote is null || param.Client.IsFakeClient)
        {
            return;
        }

        // A spectator's client doesn't send the inspect key, so spectators vote with use too (the HUD shows it).
        var select = param.Pawn.AsObserver() is null ? _config.SelectButton : _config.SelectButton | UserCommandButtons.Use;

        if ((param.KeyButtons & param.ChangedButtons & select) == 0 || (param.KeyButtons & UserCommandButtons.Speed) != 0)
        {
            return;
        }

        CastVoteFor(param.Client, _cursor[(int) param.Client.Slot]);
    }

    ECommandAction IClientListener.OnClientSayCommand(IGameClient client,
                                                      bool        teamOnly,
                                                      bool        isCommand,
                                                      string      commandName,
                                                      string      message)
    {
        if (_vote is { } vote && ChatVote(message) is var option and >= 0 && option < vote.Options.Count)
        {
            CastVoteFor(client, option);

            return ECommandAction.Handled;
        }

        switch (message.Trim().ToLowerInvariant())
        {
            case "rtv" or "rockthevote":
                OnRockTheVote(client);

                break;
            case "nominate":
                OpenNominateFromChat(client);

                break;
            case "nextmap":
                OnNextMap(client);

                break;
            case "timeleft":
                OnTimeLeft(client);

                break;
        }

        return ECommandAction.Skipped;
    }

    /// <summary>
    ///     The option a chat line votes for, from 0: "3" or "!3" is 2. Otherwise -1.
    /// </summary>
    internal static int ChatVote(string message)
    {
        var text   = message.Trim();
        var number = text.Length == 2 && ChatTriggers.Contains(text[0]) ? text[1] : text.Length == 1 ? text[0] : '\0';

        return number is >= '1' and <= '9' ? number - '1' : -1;
    }

    private void CastVoteFor(IGameClient client, int option)
    {
        if (_vote is not { } vote || !vote.Cast(client.Slot, option))
        {
            return;
        }

        _cursor[(int) client.Slot] = option;
        Changed();
        Reply(client, ChooserTexts.VoteCast, OptionLabel(client, vote.Options[option]));
    }
}
