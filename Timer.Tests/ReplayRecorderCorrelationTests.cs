using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Source2Surf.Timer;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Managers.Replay;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Modules.Practice;
using Source2Surf.Timer.Modules.Record;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Events;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Modules;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Replay;
using Source2Surf.Timer.Modules.MapInfo;
using Xunit;

namespace Timer.Tests;

public sealed class ReplayRecorderCorrelationTests
{
    [Fact]
    public void PendingReplayStoreKeepsMapAndAttemptCorrelationsIndependent()
    {
        var store = new PendingReplayStore();
        var oldKey = Key(mapId: 101, attemptId: 7);
        var otherMapKey = Key(mapId: 202, attemptId: 7);
        var laterAttemptKey = Key(mapId: 101, attemptId: 8);
        var pending = PendingReplay("surf_old");

        Assert.Null(store.Add(oldKey, pending));
        Assert.Null(store.TakeMatch(otherMapKey));
        Assert.Null(store.TakeMatch(laterAttemptKey));
        Assert.Equal(1, store.Count);

        Assert.Same(pending, store.TakeMatch(oldKey));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ReplacingPendingReplayReturnsOnlyTheSupersededEntry()
    {
        var store = new PendingReplayStore();
        var key = Key(mapId: 101, attemptId: 7);
        var first = PendingReplay("surf_old");
        var replacement = PendingReplay("surf_old");

        Assert.Null(store.Add(key, first));
        Assert.Same(first, store.Add(key, replacement));
        Assert.Equal(1, store.Count);
        Assert.Same(replacement, store.TakeMatch(key));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task LateAckAfterMapChangePromotesFallbackUnderCapturedMap(int stage)
    {
        var root = CreateTempDirectory();

        try
        {
            var oldMapName = "surf_old";
            var newMapName = "surf_new";
            var key = Key(mapId: 101, attemptId: 7, stage);
            var fallbackPath = ReplayShared.BuildReplayPath(root, oldMapName, key.Style, key.Track, key.Stage, null);
            var finalPath = ReplayShared.BuildReplayPath(root, oldMapName, key.Style, key.Track, key.Stage, 42);
            var newMapFinalPath = ReplayShared.BuildReplayPath(root, newMapName, key.Style, key.Track, key.Stage, 42);

            Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath)!);
            File.WriteAllBytes(fallbackPath, ReplayShared.SerializeReplay(
                new ReplayFileHeader { SteamId = key.SteamId, TotalFrames = 1, Time = 12.5f },
                [new ReplayFrameData()]));
            File.WriteAllText(fallbackPath + ".idx", "sidecar");

            var playback = new RecordingPlaybackModule();
            var frameAction = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var module = CreateUninitializedModule(root,
                                                   newMapName,
                                                   playback,
                                                   new CurrentMapInfoModule(mapId: 202, newMapName),
                                                   frameActionCompleted: frameAction);
            var fallbackRecords = new Dictionary<ReplayMatchKey, FallbackReplayRecord>
            {
                [key] = new FallbackReplayRecord
                {
                    TempFilePath = fallbackPath,
                    MapName      = oldMapName,
                    WriteTask    = Task.CompletedTask,
                    CreatedAt    = DateTime.UtcNow,
                },
            };
            SetField(module, "_fallbackRecords", fallbackRecords);

            var saved = new RunRecord
            {
                Id     = 42,
                MapId  = key.MapId,
                SteamId = key.SteamId,
                Style  = key.Style,
                Track  = key.Track,
                Stage  = key.Stage,
                Time   = 12.5f,
            };
            var recordEvent = new PlayerRecordSavedEvent(
                new SteamID(key.SteamId),
                "runner",
                EAttemptResult.NewPersonalRecord,
                saved,
                null,
                null,
                key.AttemptId);

            module.OnRecordSaved(recordEvent);

            Assert.False(fallbackRecords.ContainsKey(key));
            await WaitForFileAsync(finalPath);

            Assert.True(File.Exists(finalPath));
            Assert.False(File.Exists(newMapFinalPath));
            Assert.False(File.Exists(fallbackPath));
            Assert.False(File.Exists(fallbackPath + ".idx"));
            await frameAction.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, playback.Notifications);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task PendingReplayAfterMapChangeWritesOldMapFileWithoutNotifyingCurrentPlayback(int stage)
    {
        var root = CreateTempDirectory();

        try
        {
            var oldMapName = "surf_old";
            var newMapName = "surf_new";
            var key = Key(mapId: 101, attemptId: 7, stage);
            var finalPath = ReplayShared.BuildReplayPath(root, oldMapName, key.Style, key.Track, key.Stage, 43);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            var playback = new RecordingPlaybackModule();
            var frameAction = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var module = CreateUninitializedModule(root,
                                                   newMapName,
                                                   playback,
                                                   new CurrentMapInfoModule(mapId: 202, newMapName),
                                                   frameActionCompleted: frameAction);
            SetField(module, "timer_replay_file_compression_level", DispatchProxy.Create<IConVar, FixedConVarProxy>());
            SetField(module, "timer_replay_file_compression_workers", DispatchProxy.Create<IConVar, FixedConVarProxy>());

            var pending = PendingReplay(oldMapName);
            var saved = new RunRecord
            {
                Id      = 43,
                MapId   = key.MapId,
                SteamId = key.SteamId,
                Style   = key.Style,
                Track   = key.Track,
                Stage   = key.Stage,
                Time    = 12.5f,
            };
            var recordEvent = new PlayerRecordSavedEvent(
                new SteamID(key.SteamId),
                "runner",
                EAttemptResult.NewPersonalRecord,
                saved,
                null,
                null,
                key.AttemptId);
            var process = typeof(ReplayRecorderModule).GetMethod("ProcessPendingReplay",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(process);

            process!.Invoke(module, [pending, key, 43L, recordEvent]);
            await WaitForFileAsync(finalPath);

            Assert.True(File.Exists(finalPath));
            await frameAction.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, playback.Notifications);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void FallbackRecordsOlderThanTtlAreRemovedWithTheirSidecars()
    {
        var root = CreateTempDirectory();

        try
        {
            var staleKey = Key(mapId: 101, attemptId: 7);
            var freshKey = Key(mapId: 101, attemptId: 8);
            var stalePath = Path.Combine(root, "stale.tmp");
            var freshPath = Path.Combine(root, "fresh.tmp");
            File.WriteAllText(stalePath, "stale");
            File.WriteAllText(stalePath + ".idx", "stale-index");
            File.WriteAllText(freshPath, "fresh");
            File.WriteAllText(freshPath + ".idx", "fresh-index");

            var fallbackRecords = new Dictionary<ReplayMatchKey, FallbackReplayRecord>
            {
                [staleKey] = Fallback(stalePath, DateTime.UtcNow.AddMinutes(-2)),
                [freshKey] = Fallback(freshPath, DateTime.UtcNow),
            };
            var module = CreateUninitializedModule(
                root,
                "surf_current",
                clientManager: DispatchProxy.Create<IClientManager, CurrentClientManagerProxy>());
            var frameData = new PlayerFrameData?[PlayerSlot.MaxPlayerCount];
            frameData[1] = new PlayerFrameData
            {
                SteamId  = new SteamID(staleKey.SteamId),
                Name     = "runner",
                AttemptId = staleKey.AttemptId,
            };
            SetField(module, "_playerFrameData", frameData);
            SetField(module, "_fallbackRecords", fallbackRecords);
            SetField(module,
                      "timer_replay_fallback_ttl",
                      DispatchProxy.Create<IConVar, FixedConVarProxy>());

            var expire = typeof(ReplayRecorderModule).GetMethod("ExpireFallbackRecords",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(expire);
            expire!.Invoke(module, ["test"]);

            Assert.False(fallbackRecords.ContainsKey(staleKey));
            Assert.True(fallbackRecords.ContainsKey(freshKey));
            Assert.False(File.Exists(stalePath));
            Assert.False(File.Exists(stalePath + ".idx"));
            Assert.True(File.Exists(freshPath));
            Assert.True(File.Exists(freshPath + ".idx"));

            // A record ACK that arrives after cleanup must not resurrect the expired replay.
            var lateRecord = new RunRecord
            {
                Id      = 42,
                MapId   = staleKey.MapId,
                SteamId = staleKey.SteamId,
                Style   = staleKey.Style,
                Track   = staleKey.Track,
                Stage   = staleKey.Stage,
                Time    = 12.5f,
            };
            module.OnRecordSaved(new PlayerRecordSavedEvent(new SteamID(staleKey.SteamId),
                                                            "runner",
                                                            EAttemptResult.NewPersonalRecord,
                                                            lateRecord,
                                                            null,
                                                            null,
                                                            staleKey.AttemptId));
            Assert.False(fallbackRecords.ContainsKey(staleKey));
            Assert.Null(frameData[1]!.PendingMainRecordResult);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static ReplayRecorderModule CreateUninitializedModule(string replayDirectory,
                                                                  string          currentMapName,
                                                                  IReplayPlaybackModule? playback = null,
                                                                  IMapInfoModule?         mapInfo  = null,
                                                                  IClientManager?         clientManager = null,
                                                                  TaskCompletionSource<bool>? frameActionCompleted = null)
    {
        var module = (ReplayRecorderModule) RuntimeHelpers.GetUninitializedObject(typeof(ReplayRecorderModule));
        var bridge = (InterfaceBridge) RuntimeHelpers.GetUninitializedObject(typeof(InterfaceBridge));
        var modSharp = DispatchProxy.Create<IModSharp, NoopProxy>();
        ((NoopProxy)(object)modSharp).FrameActionCompleted = () => frameActionCompleted?.TrySetResult(true);
        SetBackingField(bridge, "ModSharp", modSharp);
        SetBackingField(bridge,
                        "ClientManager",
                        clientManager ?? DispatchProxy.Create<IClientManager, NoopProxy>());
        SetBackingField(bridge, "CurrentMapName", currentMapName);

        SetField(module, "_bridge", bridge);
        SetField(module, "_replayDirectory", replayDirectory);
        SetField(module, "_logger", NullLogger<ReplayRecorderModule>.Instance);
        SetField(module, "_playbackModule", playback ?? DispatchProxy.Create<IReplayPlaybackModule, NoopProxy>());
        SetField(module, "_mapInfoModule", mapInfo ?? new CurrentMapInfoModule(mapId: 1, currentMapName));
        SetField(module, "_playerFrameData", new PlayerFrameData?[PlayerSlot.MaxPlayerCount]);
        SetField(module,
                  "_replayProviderProxy",
                  (ReplayProviderProxy) RuntimeHelpers.GetUninitializedObject(typeof(ReplayProviderProxy)));
        SetField(module, "_pendingReplayStore", new PendingReplayStore());

        return module;
    }

    private static ReplayMatchKey Key(ulong mapId, int attemptId, int stage = 0)
        => new(mapId, 76561198000000001, 0, 0, stage, attemptId);

    private static PendingReplay PendingReplay(string mapName)
        => new()
        {
            Snapshot = new ReplaySaveSnapshot(new ReplayFileHeader { SteamId = 76561198000000001 }, []),
            TempFilePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tmp"),
            MapName = mapName,
        };

    private static FallbackReplayRecord Fallback(string path, DateTime createdAt)
        => new()
        {
            TempFilePath = path,
            MapName      = "surf_old",
            WriteTask    = Task.CompletedTask,
            CreatedAt    = createdAt,
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"timer-replay-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!File.Exists(path))
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Replay was not promoted to {path}.");
            }

            await Task.Delay(10);
        }
    }

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void SetBackingField(object target, string propertyName, object value)
        => target.GetType().GetField($"<{propertyName}>k__BackingField",
                                     BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            var tempRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var directory = new DirectoryInfo(fullPath);

            if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                && directory.Name.StartsWith("timer-replay-tests-", StringComparison.Ordinal)
                && directory.Attributes.HasFlag(FileAttributes.ReparsePoint) == false
                && Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup. A failed assertion should retain the path for diagnostics.
        }
    }

    private class NoopProxy : DispatchProxy
    {
        public Action? FrameActionCompleted { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                return null;
            }

            if (targetMethod.Name.Contains("InvokeFrameAction", StringComparison.Ordinal)
                && args is { Length: > 0 }
                && args[0] is Delegate callback)
            {
                var callbackResult = callback.DynamicInvoke();
                FrameActionCompleted?.Invoke();

                if (callbackResult is Task callbackTask)
                {
                    return callbackTask;
                }
            }

            if (targetMethod.ReturnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (targetMethod.ReturnType == typeof(ValueTask))
            {
                return new ValueTask();
            }

            return targetMethod.ReturnType.IsValueType
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private sealed class RecordingPlaybackModule : IReplayPlaybackModule
    {
        public int Notifications { get; private set; }

        public bool OnNewMainReplaySaved(int style,
                                         int track,
                                         ReplayContent content,
                                         ReplaySaveContext context)
        {
            Notifications++;
            return true;
        }

        public bool OnNewStageReplaySaved(int style,
                                          int track,
                                          int stage,
                                          ReplayContent content,
                                          ReplaySaveContext context)
        {
            Notifications++;
            return true;
        }

        public IReplayBotData? GetReplayBotData(PlayerSlot slot) => null;

        public IReplayBotData? GetReplayBotByIndex(int index) => null;

        public ReplayContent? GetCachedReplay(int style, int track, int stage) => null;

        public int FindClosestFrameIndex(int style,
                                         int track,
                                         int stage,
                                         in Sharp.Shared.Types.Vector position,
                                         out float distanceSquared)
        {
            distanceSquared = float.PositiveInfinity;
            return -1;
        }
    }

    private sealed class CurrentMapInfoModule : IMapInfoModule
    {
        private readonly MapProfile _profile;

        public CurrentMapInfoModule(ulong mapId, string mapName)
            => _profile = new MapProfile { MapId = mapId, MapName = mapName };

        public float GetEnterSpeedLimit(int track) => 0;

        public int GetMaxPrejumps(int track) => 0;

        public EGameMode GetCurrentGameMode() => EGameMode.None;

        public float? GetZoneExitSpeedOverride(int track) => null;

        public float GetGameModeExitSpeedLimit() => 0;

        public float GetStageEnterSpeedLimit(int track) => 0;

        public float? GetStageExitSpeedOverride(int track) => null;

        public float GetDefaultAirAccelerate() => 0;

        public MapProfile GetCurrentMapProfile() => _profile;
    }

    private class CurrentClientManagerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetGameClient")
            {
                return DispatchProxy.Create<IGameClient, CurrentClientProxy>();
            }

            return targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private class CurrentClientProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_SteamId")
            {
                return new SteamID(76561198000000001);
            }

            if (targetMethod?.Name == "get_Slot")
            {
                return new PlayerSlot(1);
            }

            return targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private class FixedConVarProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(float))
            {
                return 1.0f;
            }

            if (targetMethod?.ReturnType == typeof(double))
            {
                return 1.0d;
            }

            if (targetMethod?.ReturnType == typeof(int))
            {
                return 1;
            }

            return targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }
}
