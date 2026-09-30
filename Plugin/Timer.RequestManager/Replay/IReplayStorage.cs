using System.Threading.Tasks;

namespace Timer.RequestManager.Replay;

/// <summary>
/// Replay file storage backend abstraction.
/// Concrete implementation determined by deployment config (e.g. S3, MinIO, local file server, etc.).
/// </summary>
internal interface IReplayStorage
{
    /// <summary>
    /// Uploads replay data and returns an accessible URL.
    /// </summary>
    Task<string> UploadAsync(string key, byte[] data);

    /// <summary>
    /// Downloads replay data from the given URL.
    /// </summary>
    Task<byte[]> DownloadAsync(string url);

    /// <summary>
    /// Deletes a previously uploaded replay by its URL. Best-effort; implementations should not throw on missing files.
    /// </summary>
    Task DeleteAsync(string url);
}
