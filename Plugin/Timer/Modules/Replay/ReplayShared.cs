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
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Replay;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace Source2Surf.Timer.Modules.Replay;

internal static class ReplayShared
{
    public const char HeaderFrameSeparator = '\n';

    internal const ulong MaxDecompressedReplayBytes = 512UL * 1024 * 1024;
    public static readonly byte[] HeaderFrameSeparatorBytes = [(byte)HeaderFrameSeparator];

    /// <summary>
    ///     Where a slower run (not a new PB or WR) is kept: in a folder per player and leaderboard, named by run id,
    ///     so the newest are the highest numbers and the oldest can be pruned.
    /// </summary>
    public static string BuildRecentRunPath(string replayDirectory, string mapName, int style, int track, int stage, ulong steamId, long runId)
        => Path.Combine(RecentRunDirectory(replayDirectory, mapName, style, track, stage, steamId), $"{runId}.replay");

    private static string RecentRunDirectory(string replayDirectory, string mapName, int style, int track, int stage, ulong steamId)
    {
        var directory = Path.Combine(replayDirectory, $"style_{style}", "recent", steamId.ToString(CultureInfo.InvariantCulture), mapName, track.ToString(CultureInfo.InvariantCulture));

        return stage == 0 ? directory : Path.Combine(directory, $"stage_{stage}");
    }

    /// <summary>
    ///     Keeps a slower run's replay so its player can watch it from !replay: moves it among their recent runs on
    ///     that leaderboard and deletes all but the newest <paramref name="keep" />. With keep 0 it's just deleted.
    /// </summary>
    public static void KeepRecentRun(string filePath, string replayDirectory, string mapName, int style, int track, int stage,
                                     ulong steamId, long runId, int keep, ILogger logger)
    {
        try
        {
            if (keep <= 0)
            {
                File.Delete(filePath);

                return;
            }

            var target = BuildRecentRunPath(replayDirectory, mapName, style, track, stage, steamId, runId);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            try
            {
                File.Move(filePath, target, true);
            }
            catch (DirectoryNotFoundException) when (File.Exists(filePath))
            {
                // Removed as empty by the housekeeping sweep in between.
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(filePath, target, true);
            }

            // Its age limit counts from now.
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);

            var runs = new List<(long RunId, string Path)>();

            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(target)!, "*.replay"))
            {
                if (long.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    runs.Add((id, path));
                }
            }

            runs.Sort(static (a, b) => b.RunId.CompareTo(a.RunId));

            for (var i = keep; i < runs.Count; i++)
            {
                File.Delete(runs[i].Path);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to keep the replay of run {RunId} ({Path})", runId, filePath);
        }
    }

    /// <summary>
    ///     Deletes slower runs' replays kept since before <paramref name="cutoffUtc" />, and the folders left empty.
    /// </summary>
    public static int DeleteOldRecentRuns(string replayDirectory, DateTime cutoffUtc, ILogger logger)
    {
        var deleted = 0;

        if (!Directory.Exists(replayDirectory))
        {
            return deleted;
        }

        try
        {
            foreach (var styleDirectory in Directory.EnumerateDirectories(replayDirectory, "style_*"))
            {
                var recent = Path.Combine(styleDirectory, "recent");

                if (!Directory.Exists(recent))
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(recent, "*.replay", SearchOption.AllDirectories))
                {
                    try
                    {
                        // KeepRecentRun stamps when it was kept.
                        if (File.GetLastWriteTimeUtc(path) < cutoffUtc)
                        {
                            File.Delete(path);
                            deleted++;
                        }
                    }
                    catch (Exception e)
                    {
                        logger.LogWarning(e, "Failed to delete old replay {Path}", path);
                    }
                }

                DeleteEmptyDirectories(recent, DateTime.UtcNow.AddHours(-1), logger);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to look for old replays in {Dir}", replayDirectory);
        }

        return deleted;
    }

    // Skips folders changed in the last hour, as KeepRecentRun may have just created one to move a file into.
    // Each folder's time is read before its children are deleted, which updates it.
    private static void DeleteEmptyDirectories(string directory, DateTime unchangedSinceUtc, ILogger logger)
    {
        foreach (var child in Directory.GetDirectories(directory))
        {
            try
            {
                var changedUtc = Directory.GetLastWriteTimeUtc(child);
                DeleteEmptyDirectories(child, unchangedSinceUtc, logger);

                if (changedUtc < unchangedSinceUtc && !Directory.EnumerateFileSystemEntries(child).Any())
                {
                    Directory.Delete(child);
                }
            }
            catch (Exception e)
            {
                logger.LogDebug(e, "Failed to delete empty replay folder {Path}", child);
            }
        }
    }

    /// <summary>
    ///     A best run's replay on disk, which the cache may delete once it is in remote storage.
    /// </summary>
    internal readonly record struct CachedReplay(string Path, long RunId, long Size, DateTime LastUsedUtc);

    /// <summary>
    ///     Best runs' replays: in each style's folder and its stage folder, named after their run id. Slower runs'
    ///     replays (recent) and temp files are not part of the cache.
    /// </summary>
    public static List<CachedReplay> ListCachedReplays(string replayDirectory)
    {
        var cached = new List<CachedReplay>();

        if (!Directory.Exists(replayDirectory))
        {
            return cached;
        }

        foreach (var styleDirectory in Directory.EnumerateDirectories(replayDirectory, "style_*"))
        {
            foreach (var directory in new[] { styleDirectory, Path.Combine(styleDirectory, "stage") })
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.replay"))
                {
                    var name = Path.GetFileNameWithoutExtension(file.Name);

                    if (long.TryParse(name.AsSpan(name.LastIndexOf('_') + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var runId))
                    {
                        cached.Add(new CachedReplay(file.FullName, runId, file.Length, File.GetLastAccessTimeUtc(file.FullName)));
                    }
                }
            }
        }

        return cached;
    }

    /// <summary>
    ///     The least recently used replays to delete to get the cache within <paramref name="budgetBytes" />: only those
    ///     in remote storage, and none used since <paramref name="usedBeforeUtc" />.
    /// </summary>
    public static List<CachedReplay> ChooseEvictions(IReadOnlyCollection<CachedReplay> cached, long budgetBytes,
                                                     IReadOnlySet<long> storedRunIds, DateTime usedBeforeUtc)
    {
        var total = cached.Sum(c => c.Size);
        var evict = new List<CachedReplay>();

        foreach (var replay in cached.OrderBy(c => c.LastUsedUtc))
        {
            if (total <= budgetBytes)
            {
                break;
            }

            if (replay.LastUsedUtc < usedBeforeUtc && storedRunIds.Contains(replay.RunId))
            {
                evict.Add(replay);
                total -= replay.Size;
            }
        }

        return evict;
    }

    /// <summary>
    ///     Saves a replay downloaded from remote storage where it would be on disk, so the next load reads it from there.
    /// </summary>
    public static void CacheDownloadedReplay(string path, byte[] bytes, ILogger logger)
    {
        if (File.Exists(path))
        {
            return;
        }

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, false);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Failed to cache downloaded replay at {Path}", path);

            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // The orphaned temp file sweep removes it later.
            }
        }
    }

    // Reading doesn't reliably update the access time (often disabled), so the cache's LRU order is kept here.
    private static void MarkUsed(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception)
        {
            // Only affects which replay the cache deletes first.
        }
    }

    /// <summary>
    ///     Builds the on-disk replay path — main track (stage == 0) or stage replay. A null
    ///     <paramref name="runId" /> produces a Guid-suffixed temp name.
    /// </summary>
    public static string BuildReplayPath(string replayDirectory, string mapName, int style, int track, int stage, long? runId)
        => stage == 0
            ? BuildMainReplayPath(replayDirectory, mapName, style, track, runId)
            : BuildStageReplayPath(replayDirectory, mapName, style, track, stage, runId);

    /// <summary>
    ///     Builds a unique fallback temp path in the final replay's directory. Appends ".tmp"
    ///     instead of using Path.ChangeExtension: the temp name ends in ".replay.{guid}", so
    ///     ChangeExtension would replace the GUID and every player would share one file.
    /// </summary>
    public static string BuildFallbackTempPath(string replayDirectory, string mapName, int style, int track, int stage)
        => BuildReplayPath(replayDirectory, mapName, style, track, stage, null) + ".tmp";

    private static string BuildMainReplayPath(string replayDirectory, string mapName, int style, int track, long? runId)
    {
        var fileName = runId is null
            ? $"{mapName}_{track}.replay.{Guid.NewGuid()}"
            : $"{mapName}_{track}_{runId.Value}.replay";

        return Path.Combine(replayDirectory,
                            $"style_{style}",
                            fileName);
    }

    private static string BuildStageReplayPath(string replayDirectory, string mapName, int style, int track, int stage, long? runId)
    {
        var fileName = runId is null
            ? $"{mapName}_{track}_{stage}.replay.{Guid.NewGuid()}"
            : $"{mapName}_{track}_{stage}_{runId.Value}.replay";

        return Path.Combine(replayDirectory,
                            $"style_{style}",
                            "stage",
                            fileName);
    }

    /// <summary>
    /// Serialize replay data in-memory (JSON header + \n separator + Zstd-compressed MemoryPack frame data)
    /// for remote upload. Spatial fields are quantized for storage (see <see cref="ReplayFrameStorage"/>).
    /// </summary>
    public static byte[] SerializeReplay(ReplayFileHeader header, IReadOnlyList<ReplayFrameData> frames)
    {
        var storage = new ReplayFrameStorage(frames);
        var storageHeader = header with { Version = storage.Version };
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(storageHeader);
        var payloadSize = storage.SerializedSize;
        var payload = ArrayPool<byte>.Shared.Rent(payloadSize);
        try
        {
            storage.Serialize(payload.AsSpan(0, payloadSize));
            var prefixSize = checked(headerBytes.Length + 1);
            var compressedCapacity = Compressor.GetCompressBound(payloadSize);
            var output = ArrayPool<byte>.Shared.Rent(checked(prefixSize + compressedCapacity));
            try
            {
                headerBytes.CopyTo(output, 0);
                output[headerBytes.Length] = (byte)HeaderFrameSeparator;
                using var compressor = new Compressor();
                compressor.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
                var written = compressor.Wrap(payload.AsSpan(0, payloadSize),
                                              output.AsSpan(prefixSize, compressedCapacity));

                // The returned upload byte[] owns its memory. All working buffers are pooled.
                return output.AsSpan(0, prefixSize + written).ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(output);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    /// <summary>
    /// Deserialize replay from raw bytes (JSON header + \n + Zstd-compressed MemoryPack frame data).
    /// Used for remote replay data deserialization.
    /// </summary>
    public static ReplayLoadResult? DeserializeReplay(ReadOnlySpan<byte> bytes, int style, int track, int stage, ILogger logger)
    {
        try
        {
            var split = bytes.IndexOf((byte)HeaderFrameSeparator);

            if (split == -1)
            {
                logger.LogError("Invalid replay data: missing header separator for style={style} track={track} stage={stage}", style, track, stage);
                return null;
            }

            var header = JsonSerializer.Deserialize<ReplayFileHeader>(bytes[..split]);
            if (header == null)
            {
                logger.LogError("Failed to deserialize replay header for style={style} track={track} stage={stage}", style, track, stage);
                return null;
            }

            using var decompressor = new Decompressor();
            var frames = DeserializeReplayFrames(bytes[(split + 1)..], header.Version, decompressor);
            if (frames == null)
            {
                logger.LogError("Failed to deserialize replay frames for style={style} track={track} stage={stage}", style, track, stage);
                return null;
            }

            return new ReplayLoadResult(style, track, stage, new ReplayContent { Header = header, Frames = frames });
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error deserializing replay for style={style} track={track} stage={stage}", style, track, stage);
            return null;
        }
    }

    /// <summary>
    /// Load a replay file from the given path, reusing a Decompressor instance.
    /// Corrupted files (missing HeaderFrameSeparator) are renamed with a .corrupt suffix as backup.
    /// </summary>
    public static ReplayLoadResult? LoadReplayFromPath(string path, int style, int track, int stage, Decompressor decompressor, ILogger logger)
    {
        byte[]? fileBuffer = null;
        try
        {
            int length;
            // Close the handle before parsing so corrupt files can still be renamed on Windows.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                               bufferSize: 1, FileOptions.SequentialScan))
            {
                if (stream.Length > Array.MaxLength)
                    throw new InvalidDataException("Replay file exceeds the supported buffer size.");
                length = checked((int)stream.Length);
                fileBuffer = ArrayPool<byte>.Shared.Rent(length);
                stream.ReadExactly(fileBuffer.AsSpan(0, length));
            }
            var bytes = fileBuffer.AsSpan(0, length);
            var split = bytes.IndexOf((byte)HeaderFrameSeparator);

            if (split == -1)
            {
                logger.LogError("Invalid replay file at {path}, renaming to .corrupt", path);

                try
                {
                    File.Move(path, path + ".corrupt", true);
                }
                catch (Exception renameEx)
                {
                    logger.LogError(renameEx, "Failed to rename corrupt replay file: {path}", path);
                }

                return null;
            }

            var header = JsonSerializer.Deserialize<ReplayFileHeader>(bytes[..split]);
            if (header == null)
            {
                logger.LogError("Failed to deserialize header: {p}", path);
                return null;
            }

            var frames = DeserializeReplayFrames(bytes[(split + 1)..], header.Version, decompressor);
            if (frames == null)
            {
                logger.LogError("Failed to deserialize frames: {p}", path);
                return null;
            }

            MarkUsed(path);

            return new ReplayLoadResult(style, track, stage, new ReplayContent { Header = header, Frames = frames });
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error loading replay: {p}", path);
            return null;
        }
        finally
        {
            if (fileBuffer is not null)
                ArrayPool<byte>.Shared.Return(fileBuffer);
        }
    }

    /// <summary>
    /// A player's recording buffer to start with: 64 KB, below the large object heap. It grows with their runs.
    /// </summary>
    public const int InitialFrames = 1024;

    /// <summary>
    /// Create a main replay snapshot from PlayerFrameData.
    /// Resets the frame buffer and timer state on the source frame data.
    /// </summary>
    public static ReplaySaveSnapshot CreateMainReplaySnapshot(PlayerFrameData frame)
    {
        // The replay gets an exact copy and the buffer is kept for the next run, at its size unless it grew far past
        // this run (a long warm-up, then short attempts).
        var buffer       = frame.Frames;
        var framesBuffer = buffer.ToArray();
        buffer.Clear();

        if (buffer.Capacity > 4 * Math.Max(framesBuffer.Length, InitialFrames))
        {
            buffer.Capacity = Math.Max(framesBuffer.Length, InitialFrames);
        }

        var header = new ReplayFileHeader
        {
            SteamId     = frame.SteamId,
            TotalFrames = framesBuffer.Length,
            PreFrame    = frame.TimerStartFrame,
            PostFrame   = frame.TimerFinishFrame,
            Time        = frame.FinishTime,
            StageTicks  = [..frame.NewStageTicks],
            PlayerName  = frame.Name,
        };

        frame.NewStageTicks.Clear();
        frame.StageTimerStartTicks.Clear();
        frame.TimerStartFrame  = 0;
        frame.TimerFinishFrame = 0;
        frame.FinishTime       = 0;

        return new ReplaySaveSnapshot(header, framesBuffer);
    }

    /// <summary>
    /// Create a stage replay snapshot from PlayerFrameData.
    /// Materializes the [startTick, startTick+length) range into a private array so the
    /// snapshot never aliases the player's live, still-mutating Frames list.
    /// </summary>
    public static ReplaySaveSnapshot CreateStageReplaySnapshot(PlayerFrameData frame,
                                                               int             startTick,
                                                               int             stageStartFrame,
                                                               int             stageFinishFrame,
                                                               int             postRunFrameCount,
                                                               float           finishTime)
    {
        var finalFrame = Math.Min(frame.Frames.Count, stageFinishFrame + postRunFrameCount);
        var length     = Math.Max(0, finalFrame                        - startTick);

        // Copy the frames out NOW, on the (main-thread) caller. Unlike the main path —
        // which detaches its buffer by swapping in a fresh list (CreateMainReplaySnapshot) —
        // the stage path can't swap because the run continues, so a zero-copy slice over
        // frame.Frames would be read by the background serializer while OnPlayerRunCommandPost
        // keeps appending and TrimPreRunFrames shrinks the same List<T>: a torn-read / shifted-
        // frame data race. A private array makes the snapshot immutable and self-contained.
        ReplayFrameData[] framesToWrite;

        if (length == 0)
        {
            framesToWrite = [];
        }
        else
        {
            framesToWrite = new ReplayFrameData[length];
            frame.Frames.CopyTo(startTick, framesToWrite, 0, length);
        }

        // Clamp the marker fields to the materialized length so they can never index past the
        // written frames (e.g. when the post-run pushed stageFinishFrame beyond what was recorded).
        var preFrame  = Math.Clamp(stageStartFrame  - startTick, 0, length);
        var postFrame = Math.Clamp(stageFinishFrame - startTick, preFrame, length);

        var header = new ReplayFileHeader
        {
            SteamId     = frame.SteamId,
            TotalFrames = framesToWrite.Length,
            PreFrame    = preFrame,
            PostFrame   = postFrame,
            Time        = finishTime,
            PlayerName  = frame.Name,
        };

        return new ReplaySaveSnapshot(header, framesToWrite);
    }

    /// <summary>
    /// Trim pre-run frame data, keeping only the most recent maxPreFrame frames.
    /// </summary>
    public static void TrimPreRunFrames(PlayerFrameData frameData, int maxPreFrame)
    {
        if (maxPreFrame <= 0)
        {
            frameData.Frames.Clear();

            return;
        }

        var excess = frameData.Frames.Count - maxPreFrame;

        if (excess > 0)
        {
            frameData.Frames.RemoveRange(0, excess);
        }
    }

    /// <summary>
    /// Bounds the buffer of a player with no run in progress, keeping the most recent
    /// maxPreFrame frames as pre-run data. Unlike <see cref="TrimPreRunFrames"/>, which runs
    /// at timer start and resets the per-run indices itself, this shifts the stored frame
    /// indices so they keep pointing at the same frames (clamped at 0 for dropped ones).
    /// Callers must only use it while no main/stage run or post-run capture is in progress.
    /// </summary>
    public static void TrimIdleFrames(PlayerFrameData frameData, int maxPreFrame)
    {
        var excess = frameData.Frames.Count - Math.Max(maxPreFrame, 0);

        if (excess <= 0)
        {
            return;
        }

        frameData.Frames.RemoveRange(0, excess);
        ShiftFrameIndices(frameData.NewStageTicks, excess);
        ShiftFrameIndices(frameData.StageTimerStartTicks, excess);
        frameData.TimerStartFrame  = Math.Max(0, frameData.TimerStartFrame  - excess);
        frameData.TimerFinishFrame = Math.Max(0, frameData.TimerFinishFrame - excess);
    }

    private static void ShiftFrameIndices(List<int> indices, int removed)
    {
        for (var i = 0; i < indices.Count; i++)
        {
            indices[i] = Math.Max(0, indices[i] - removed);
        }
    }

    /// <summary>
    /// Capture the current end of the recording for <see cref="TryRewindFrames"/>. The stage lists are copied because
    /// re-entering a stage overwrites its StageTimerStartTicks entry in place.
    /// </summary>
    public static ReplayMark CreateMark(PlayerFrameData frameData)
        => new (frameData.Lineage,
                frameData.Frames.Count,
                [.. frameData.NewStageTicks],
                [.. frameData.StageTimerStartTicks]);

    /// <summary>
    /// Whether the recording still holds every frame up to <paramref name="mark"/>: same lineage, and not already
    /// shorter than it.
    /// </summary>
    public static bool CanRewind(PlayerFrameData frameData, ReplayMark mark)
        => mark.Lineage == frameData.Lineage && mark.FrameCount <= frameData.Frames.Count;

    /// <summary>
    /// Drop every frame recorded after <paramref name="mark"/> and restore the stage bookkeeping from then.
    /// False, leaving the recording untouched, when <see cref="CanRewind"/> is false.
    /// </summary>
    public static bool TryRewindFrames(PlayerFrameData frameData, ReplayMark mark)
    {
        if (!CanRewind(frameData, mark))
        {
            return false;
        }

        frameData.Frames.RemoveRange(mark.FrameCount, frameData.Frames.Count - mark.FrameCount);

        frameData.NewStageTicks.Clear();
        frameData.NewStageTicks.AddRange(mark.NewStageTicks);

        frameData.StageTimerStartTicks.Clear();
        frameData.StageTimerStartTicks.AddRange(mark.StageTimerStartTicks);

        return true;
    }

    /// <summary>
    /// Ensure the replay directory structure exists (style and stage subdirectories).
    /// Creates style_0 through style_{MAX_STYLE-1} directories, each with a stage subdirectory.
    /// </summary>
    public static void EnsureReplayDirectories(string replayDirectory)
    {
        if (!Directory.Exists(replayDirectory))
        {
            Directory.CreateDirectory(replayDirectory);
        }

        // path/data/surftimer/replays/style_id/mapname_tracknum.replay
        // path/data/surftimer/replays/style_id/stage/mapname_tracknum_stagenum.replay
        for (var i = 0; i < TimerConstants.MAX_STYLE; i++)
        {
            var stylePath = Path.Combine(replayDirectory, $"style_{i}");

            if (!Directory.Exists(stylePath))
            {
                Directory.CreateDirectory(stylePath);
            }

            var stagePath = Path.Combine(stylePath, "stage");

            if (!Directory.Exists(stagePath))
            {
                Directory.CreateDirectory(stagePath);
            }
        }
    }

    /// <summary>
    /// Load ReplayBotConfig array from a JSON file.
    /// Generates a default config file if it doesn't exist.
    /// Falls back to defaults and logs a warning if deserialization fails.
    /// </summary>
    public static ReplayBotConfig[] LoadReplayBotConfigs(string configPath, ILogger logger)
    {
        var defaultConfigs = new ReplayBotConfig[]
        {
            new (),
            new () { Type = EReplayBotType.Central, IdleName = "Replay Bot (!replay)", SpectateWhenIdle = true },
        };

        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfigs, Utils.SerializerOptions));

            logger.LogWarning("Failed to find replay config at {path}, generating the default one...", configPath);

            return defaultConfigs;
        }

        try
        {
            var configs
                = JsonSerializer.Deserialize<ReplayBotConfig[]>(File.ReadAllText(configPath),
                                                                Utils.DeserializerOptions);

            if (configs == null || configs.Length == 0)
            {
                logger.LogWarning("Failed to deserialize replay config, regenerate with default config");
                File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfigs, Utils.SerializerOptions));

                return defaultConfigs;
            }

            return configs;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to deserialize replay config, regenerate with default config");
            File.WriteAllText(configPath, JsonSerializer.Serialize(defaultConfigs, Utils.SerializerOptions));

            return defaultConfigs;
        }
    }

    /// <summary>
    /// Asynchronously write a replay file (JSON header + \n separator + MemoryPack frame data).
    /// Spatial fields are quantized for storage (see <see cref="ReplayFrameStorage"/>).
    /// If compressionLevel &lt;= 0, writes uncompressed frame data.
    /// If compressionWorkers &lt;= 0, uses single-threaded compression.
    /// </summary>
    public static async Task<bool> WriteReplayToFileAsync(
        ReplayFileHeader header,
        string path,
        IReadOnlyList<ReplayFrameData> framesToWrite,
        int compressionLevel,
        int compressionWorkers,
        ILogger logger)
    {
        try
        {
            var storage = new ReplayFrameStorage(framesToWrite);
            var storageHeader = header with { Version = storage.Version };

            await using var fileStream
                = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);

            await JsonSerializer.SerializeAsync(fileStream, storageHeader).ConfigureAwait(false);
            await fileStream.WriteAsync(HeaderFrameSeparatorBytes).ConfigureAwait(false);

            if (compressionLevel <= 0)
            {
                await storage.SerializeAsync(fileStream);
            }
            else
            {
                await using var compressionStream = new CompressionStream(fileStream, compressionLevel);

                compressionStream.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers,
                                               Math.Max(compressionWorkers, 0));
                // 4 bytes per file; a damaged file then fails to load instead of playing wrong frames.
                compressionStream.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag, 1);

                await storage.SerializeAsync(compressionStream);
            }
        }
        catch (Exception e)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            logger.LogError(e, "Error when trying to write replay file to {p}", path);

            return false;
        }

        return true;
    }

    private static ReplayFrameData[]? DeserializeReplayFrames(ReadOnlySpan<byte> payload, int version, Decompressor decompressor)
    {
        ulong capacity;
        try
        {
            // For streaming Zstd frames this is a bound, not necessarily the exact length.
            capacity = Decompressor.GetDecompressedSize(payload);
        }
        catch (ZstdException)
        {
            // Legacy and current files may be stored without Zstd compression.
            return ReplayFrameStorage.Deserialize(payload, version);
        }

        // The declared size comes from the (possibly remote or corrupt) payload itself. A 24-hour
        // 64-tick run is ~5.5M frames, a few hundred MB, so a larger claim is not a real replay
        // and must not make us rent a multi-GB buffer.
        if (capacity > MaxDecompressedReplayBytes)
            throw new InvalidDataException("Decompressed replay exceeds the supported buffer size.");

        var buffer = ArrayPool<byte>.Shared.Rent((int)capacity);
        try
        {
            var written = decompressor.Unwrap(payload, buffer.AsSpan(0, (int)capacity));
            return ReplayFrameStorage.Deserialize(buffer.AsSpan(0, written), version);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
