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
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Shared.Events;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Modules;

internal interface ISoundModule
{
}

/// <summary>
///     Finish sounds: a server record plays to everyone (a stage one to its player), a personal best or another finish
///     to its player. !sounds turns them off for a player, as a player setting.
/// </summary>
internal class SoundModule : IModule, ISoundModule, IRecordModuleListener
{
    private readonly InterfaceBridge       _bridge;
    private readonly IRecordModule         _recordModule;
    private readonly ICommandManager       _commandManager;
    private readonly ILocalizationProvider _localization;
    private readonly IPlayerSettings       _settings;

    // ReSharper disable InconsistentNaming

    private readonly IConVar timer_sound_sr;
    private readonly IConVar timer_sound_pb;
    private readonly IConVar timer_sound_finish;

    // ReSharper restore InconsistentNaming

    public SoundModule(InterfaceBridge       bridge,
                       IRecordModule         recordModule,
                       ICommandManager       commandManager,
                       ILocalizationProvider localization,
                       IPlayerSettings       settings)
    {
        _bridge         = bridge;
        _recordModule   = recordModule;
        _commandManager = commandManager;
        _localization   = localization;
        _settings       = settings;

        timer_sound_sr = bridge.ConVarManager.CreateConVar("timer_sound_sr",
                                                           "UIPanorama.XP.NewSkillGroup",
                                                           "Sound event played to everyone on a new server record, empty for none")!;

        timer_sound_pb = bridge.ConVarManager.CreateConVar("timer_sound_pb",
                                                           "UIPanorama.XP.NewRank",
                                                           "Sound event played to the player on a new personal best, empty for none")!;

        timer_sound_finish = bridge.ConVarManager.CreateConVar("timer_sound_finish",
                                                               "UI.XP.Milestone_01",
                                                               "Sound event played to the player on a finish that isn't a personal best, empty for none")!;
    }

    public bool Init()
    {
        _recordModule.RegisterListener(this);
        _commandManager.AddClientChatCommand("sounds", OnCommandSounds);

        return true;
    }

    public void Shutdown()
    {
        _recordModule.UnregisterListener(this);
    }

    public void OnRecordSaved(PlayerRecordSavedEvent recordEvent)
    {
        switch (recordEvent.RecordType)
        {
            case EAttemptResult.NewServerRecord when !recordEvent.IsStageRecord:
                PlayToAll(timer_sound_sr.GetString());

                break;
            case EAttemptResult.NewServerRecord:
                PlayTo(recordEvent.SteamId, timer_sound_sr.GetString());

                break;
            case EAttemptResult.NewPersonalRecord:
                PlayTo(recordEvent.SteamId, timer_sound_pb.GetString());

                break;
            case EAttemptResult.NoNewRecord when !recordEvent.IsStageRecord:
                PlayTo(recordEvent.SteamId, timer_sound_finish.GetString());

                break;
        }
    }

    // One sound for everyone who has them on.
    private void PlayToAll(string sound)
    {
        if (sound.Length == 0)
        {
            return;
        }

        ulong players = 0;

        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (!client.IsFakeClient && !client.IsHltv && _settings.PlaysSounds(client.Slot))
            {
                players |= 1UL << client.Slot;
            }
        }

        if (players != 0)
        {
            _bridge.SoundManager.StartSoundEvent(sound, filter: new RecipientFilter(players));
        }
    }

    private void PlayTo(SteamID steamId, string sound)
    {
        if (sound.Length > 0 && _bridge.ClientManager.GetGameClient(steamId) is { } client)
        {
            Play(client, sound);
        }
    }

    private void Play(IGameClient client, string sound)
    {
        if (!client.IsFakeClient && !client.IsHltv && _settings.PlaysSounds(client.Slot))
        {
            client.GetPlayerController()?.EmitSoundClient(sound);
        }
    }

    private ECommandAction OnCommandSounds(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var on = !_settings.PlaysSounds(slot);
        _settings.SetPlaysSounds(slot, on);

        controller.PrintToChat(_localization.For(slot)[on ? ChatTexts.SoundsOn : ChatTexts.SoundsOff]);

        return ECommandAction.Handled;
    }
}
