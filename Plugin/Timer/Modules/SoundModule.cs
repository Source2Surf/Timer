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
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
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
///     Finish sounds from timer-sounds.jsonc: a rank sound or a server record plays to everyone (a stage record to its
///     player), the rest to the player. !sounds turns them off for a player, as a player setting.
/// </summary>
internal class SoundModule : IModule, ISoundModule, IRecordModuleListener, IGameListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge       _bridge;
    private readonly IRecordModule         _recordModule;
    private readonly ICommandManager       _commandManager;
    private readonly ILocalizationProvider _localization;
    private readonly IPlayerSettings       _settings;
    private readonly ILogger<SoundModule>  _logger;
    private readonly string                _configPath;

    private SoundConfig _config = new ();

    public SoundModule(InterfaceBridge       bridge,
                       IRecordModule         recordModule,
                       ICommandManager       commandManager,
                       ILocalizationProvider localization,
                       IPlayerSettings       settings,
                       ILogger<SoundModule>  logger)
    {
        _bridge         = bridge;
        _recordModule   = recordModule;
        _commandManager = commandManager;
        _localization   = localization;
        _settings       = settings;
        _logger         = logger;
        _configPath     = Path.Combine(bridge.SharpPath, "configs", "timer-sounds.jsonc");
    }

    public bool Init()
    {
        _config = SoundConfig.Load(_configPath, _logger);

        _recordModule.RegisterListener(this);
        _bridge.ModSharp.InstallGameListener(this);
        _commandManager.AddClientChatCommand("sounds", OnCommandSounds);

        return true;
    }

    public void Shutdown()
    {
        _recordModule.UnregisterListener(this);
        _bridge.ModSharp.RemoveGameListener(this);
    }

    // Each map reads the config again, so edits apply from the next map.
    public void OnResourcePrecache()
    {
        _config = SoundConfig.Load(_configPath, _logger);

        foreach (var file in _config.Precache)
        {
            _bridge.ModSharp.PrecacheResource(file);
        }
    }

    public void OnRecordSaved(PlayerRecordSavedEvent recordEvent)
    {
        var steamId = recordEvent.SteamId;

        switch (recordEvent.RecordType)
        {
            case EAttemptResult.NoNewRecord when !recordEvent.IsStageRecord:
                PlayTo(steamId, _config.NoImprovement);

                break;
            case EAttemptResult.NewServerRecord when recordEvent.IsStageRecord:
                PlayTo(steamId, _config.ServerRecord.Count > 0 ? _config.ServerRecord : _config.PersonalBest);

                break;
            case EAttemptResult.NewPersonalRecord when recordEvent.IsStageRecord:
                PlayTo(steamId, _config.PersonalBest);

                break;
            case >= EAttemptResult.NewPersonalRecord:
                var first         = recordEvent.PbRecord is null;
                var (rank, total) = Rank(recordEvent.SavedRecord, first);
                var (sounds, all) = _config.ForBest(recordEvent.RecordType == EAttemptResult.NewServerRecord, first, rank, total);

                if (all)
                {
                    PlayToAll(sounds);
                }
                else
                {
                    PlayTo(steamId, sounds);
                }

                break;
        }
    }

    // The leaderboard is refreshed after this event, so it doesn't hold the new run yet: a first finish adds one.
    private (int rank, int total) Rank(RunRecord record, bool first)
    {
        try
        {
            return (_recordModule.GetRankForTime(record.Style, record.Track, record.Time),
                    _recordModule.GetTotalRecordCount(record.Style, record.Track) + (first ? 1 : 0));
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }

    private static string? Pick(List<string> sounds)
        => sounds.Count switch
        {
            0 => null,
            1 => sounds[0],
            _ => sounds[Random.Shared.Next(sounds.Count)],
        };

    // One sound for everyone who has them on.
    private void PlayToAll(List<string> sounds)
    {
        if (Pick(sounds) is not { Length: > 0 } sound)
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

    private void PlayTo(SteamID steamId, List<string> sounds)
    {
        if (Pick(sounds) is { Length: > 0 } sound
            && _bridge.ClientManager.GetGameClient(steamId) is { IsFakeClient: false, IsHltv: false } client
            && _settings.PlaysSounds(client.Slot))
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

/// <summary>
///     timer-sounds.jsonc: sound events by finish. Each is a list played one at random; empty plays none.
/// </summary>
internal sealed class SoundConfig
{
    [JsonPropertyName("server_record")]
    public List<string> ServerRecord { get; set; } = ["UIPanorama.XP.NewSkillGroup"];

    [JsonPropertyName("personal_best")]
    public List<string> PersonalBest { get; set; } = ["UIPanorama.XP.NewRank"];

    [JsonPropertyName("first_finish")]
    public List<string> FirstFinish { get; set; } = [];

    [JsonPropertyName("no_improvement")]
    public List<string> NoImprovement { get; set; } = ["UI.XP.Milestone_01"];

    [JsonPropertyName("worst")]
    public List<string> Worst { get; set; } = [];

    [JsonPropertyName("worst_minimum")]
    public int WorstMinimum { get; set; } = 10;

    [JsonPropertyName("ranks")]
    public Dictionary<int, List<string>> Ranks { get; set; } = new ();

    // .vsndevts files with the server's own sound events.
    [JsonPropertyName("precache")]
    public List<string> Precache { get; set; } = [];

    // A track's new best, and whether everyone hears it: a rank sound, then a server record, the slowest time, a first
    // finish, a personal best; an empty one falls through.
    public (List<string> Sounds, bool Everyone) ForBest(bool serverRecord, bool first, int rank, int total)
        => rank > 0 && Ranks.TryGetValue(rank, out var sounds) && sounds.Count > 0 ? (sounds, true)
            : serverRecord && ServerRecord.Count > 0                              ? (ServerRecord, true)
            : rank > 0 && rank == total && total >= WorstMinimum && Worst.Count > 0 ? (Worst, false)
            : first && FirstFinish.Count > 0                                       ? (FirstFinish, false)
                                                                                     : (PersonalBest, false);

    public static SoundConfig Load(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<SoundConfig>(File.ReadAllText(path), Utils.DeserializerOptions) is { } config)
            {
                return config;
            }

            var defaults = new SoundConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, Utils.SerializerOptions));

            return defaults;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to read {path}, using the default sounds", path);

            return new SoundConfig();
        }
    }
}
