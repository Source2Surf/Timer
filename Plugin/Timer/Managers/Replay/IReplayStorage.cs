using System.IO;
using System.Threading.Tasks;

namespace Source2Surf.Timer.Managers.Replay;

/// <summary>
/// Replay file storage backend abstraction.
/// Concrete implementation determined by deployment config (e.g. S3, MinIO, local file server, etc.).
/// </summary>
internal interface IReplayStorage
{
    /// <summary>
    /// Uploads replay data and returns an accessible URL. <paramref name="data"/> is sent as it's read, from its
    /// position to its end, so a replay file never has to be all in memory. It must be seekable: the request then
    /// carries a Content-Length, and a retried request can read it again.
    /// </summary>
    Task<string> UploadAsync(string key, Stream data);

    /// <summary>
    /// Downloads replay data from the given URL.
    /// </summary>
    Task<byte[]> DownloadAsync(string url);

    /// <summary>
    /// Deletes a previously uploaded replay by its URL. Best-effort; implementations should not throw on missing files.
    /// </summary>
    Task DeleteAsync(string url);
}
