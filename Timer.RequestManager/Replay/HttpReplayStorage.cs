using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Timer.RequestManager.Replay;

/// <summary>
/// HTTP-based replay storage implementation.
/// Uploads and downloads replay files via a configurable Base URL.
/// </summary>
internal sealed class HttpReplayStorage : IReplayStorage
{
    private readonly HttpClient _httpClient;
    private readonly string     _baseUrl;

    public HttpReplayStorage(HttpClient httpClient, string baseUrl)
    {
        _httpClient = httpClient;
        _baseUrl    = baseUrl.TrimEnd('/');
    }

    public async Task<string> UploadAsync(string key, byte[] data)
    {
        var url = $"{_baseUrl}/{key}";

        using var content  = new ByteArrayContent(data);
        using var response = await _httpClient.PutAsync(url, content);

        response.EnsureSuccessStatusCode();

        return url;
    }

    /// <summary>
    /// A 24-hour 64-tick replay is a few hundred MB uncompressed and far less compressed.
    /// Refuse anything larger rather than buffering an arbitrary response in memory.
    /// </summary>
    internal const long MaxDownloadBytes = 256L * 1024 * 1024;

    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromSeconds(100);

    public async Task<byte[]> DownloadAsync(string url)
    {
        // HttpClient.Timeout covers only the headers once ResponseHeadersRead is used, so bound
        // the whole transfer explicitly; a stalled body would otherwise hang this load forever.
        using var deadline = new CancellationTokenSource(
            _httpClient.Timeout == Timeout.InfiniteTimeSpan ? DefaultDownloadTimeout : _httpClient.Timeout);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength > MaxDownloadBytes)
        {
            throw new InvalidDataException($"Replay at {url} exceeds {MaxDownloadBytes} bytes.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var buffer = new MemoryStream(declaredLength is { } length ? (int)length : 0);
        var chunk = new byte[81_920];
        int read;

        // Content-Length may be absent or wrong (chunked transfer), so also bound what is read.
        while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
        {
            if (buffer.Length + read > MaxDownloadBytes)
            {
                throw new InvalidDataException($"Replay at {url} exceeds {MaxDownloadBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        // When Content-Length was accurate the pre-sized buffer is exactly full: hand it out
        // instead of copying a potentially large replay a second time.
        return buffer.Length == buffer.Capacity ? buffer.GetBuffer() : buffer.ToArray();
    }

    public async Task DeleteAsync(string url)
    {
        try
        {
            using var response = await _httpClient.DeleteAsync(url);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException)
        {
            // Best-effort: file may already be gone or endpoint may not support DELETE.
        }
    }
}
