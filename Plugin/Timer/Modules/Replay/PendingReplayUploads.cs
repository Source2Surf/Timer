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
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

// A best run's replay that couldn't be uploaded (the replay store or backend down) is tried again until it goes through,
// across restarts: a marker beside the replay file keeps it. Slower runs' uploads are optional and aren't kept.
internal partial class ReplayRecorderModule
{
    private const string PendingUploadSuffix  = ".upload";
    private const double UploadRetrySeconds   = 60;
    private const double UploadRetryMaxSeconds = 600;

    private sealed record PendingUpload(string Map, int Style, int Track, int Stage, ulong SteamId, ulong RunId);

    // By replay path.
    private readonly ConcurrentDictionary<string, PendingUpload> _pendingUploads = new (StringComparer.OrdinalIgnoreCase);

    // The retry loop runs on the thread pool: nothing it touches needs the game thread.
    private readonly CancellationTokenSource _uploadRetry = new ();

    private void QueueUploadRetry(string replayPath, PendingUpload upload)
    {
        try
        {
            File.WriteAllText(replayPath + PendingUploadSuffix, JsonSerializer.Serialize(upload));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Couldn't keep the failed upload of {Path} for a retry after a restart", replayPath);
        }

        _pendingUploads[replayPath] = upload;
        _logger.LogWarning("The replay of run {RunId} will be uploaded again when the replay store is back", upload.RunId);
    }

    // At map start, off the game thread: the uploads left from before.
    private void LoadPendingUploads()
    {
        var directory = _replayDirectory;

        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return;
                }

                foreach (var marker in Directory.EnumerateFiles(directory, "*" + PendingUploadSuffix, SearchOption.AllDirectories))
                {
                    var replayPath = marker[..^PendingUploadSuffix.Length];

                    if (!File.Exists(replayPath))
                    {
                        File.Delete(marker);

                        continue;
                    }

                    if (JsonSerializer.Deserialize<PendingUpload>(File.ReadAllText(marker)) is { } upload)
                    {
                        _pendingUploads[replayPath] = upload;
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to read the pending replay uploads");
            }
        });
    }

    // Every minute; while the store stays down, less often, up to every ten minutes.
    private async Task RetryUploadsAsync(CancellationToken token)
    {
        var delay = UploadRetrySeconds;

        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = await RetryUploadsOnceAsync().ConfigureAwait(false)
                ? UploadRetrySeconds
                : Math.Min(delay * 2, UploadRetryMaxSeconds);
        }
    }

    // False when an upload failed: the rest wait for the next pass.
    private async Task<bool> RetryUploadsOnceAsync()
    {
        if (_pendingUploads.IsEmpty || !_replayProviderProxy.IsAvailable)
        {
            return true;
        }

        try
        {
            foreach (var (replayPath, upload) in _pendingUploads)
            {
                if (!File.Exists(replayPath))
                {
                    ForgetUpload(replayPath);

                    continue;
                }

                var bytes = await File.ReadAllBytesAsync(replayPath).ConfigureAwait(false);

                try
                {
                    await UploadReplayAsync(_replayProviderProxy,
                                            upload.Map,
                                            upload.Style,
                                            upload.Track,
                                            upload.Stage,
                                            upload.SteamId,
                                            upload.RunId,
                                            bytes)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return false; // logged where it failed
                }

                ForgetUpload(replayPath);
                _logger.LogInformation("Uploaded the replay of run {RunId} after an earlier failure", upload.RunId);
            }

            return true;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Retrying the pending replay uploads failed");

            return false;
        }
    }

    private void ForgetUpload(string replayPath)
    {
        _pendingUploads.TryRemove(replayPath, out _);

        try
        {
            File.Delete(replayPath + PendingUploadSuffix);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to delete the pending upload marker of {Path}", replayPath);
        }
    }
}
