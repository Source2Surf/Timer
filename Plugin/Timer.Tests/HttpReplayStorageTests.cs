using System.Net;
using System.Net.Http.Headers;
using Source2Surf.Timer.Managers.Replay;
using Xunit;

namespace Timer.Tests;

public sealed class HttpReplayStorageTests
{
    [Fact]
    public async Task DownloadReturnsANormalReplay()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        using var client = new HttpClient(new FixedResponseHandler(() => new ByteArrayContent(bytes)));
        var storage = new HttpReplayStorage(client, "https://replays.test");

        Assert.Equal(bytes, await storage.DownloadAsync("https://replays.test/a.replay"));
    }

    [Fact]
    public async Task DownloadRejectsADeclaredOversizedReplayWithoutReadingIt()
    {
        using var client = new HttpClient(new FixedResponseHandler(() =>
        {
            var content = new StreamContent(new MemoryStream());
            content.Headers.ContentLength = HttpReplayStorage.MaxDownloadBytes + 1;
            return content;
        }));
        var storage = new HttpReplayStorage(client, "https://replays.test");

        await Assert.ThrowsAsync<InvalidDataException>(() => storage.DownloadAsync("https://replays.test/huge.replay"));
    }

    [Fact]
    public async Task DownloadRejectsAnOversizedReplayWithoutAContentLength()
    {
        using var client = new HttpClient(new FixedResponseHandler(() =>
        {
            // Chunked-style body with no Content-Length: the limit must hold while streaming.
            var content = new StreamContent(new EndlessStream());
            content.Headers.ContentLength = null;
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return content;
        }));
        var storage = new HttpReplayStorage(client, "https://replays.test");

        await Assert.ThrowsAsync<InvalidDataException>(() => storage.DownloadAsync("https://replays.test/endless.replay"));
    }

    [Fact]
    public async Task DownloadTimesOutWhenTheBodyStalls()
    {
        using var client = new HttpClient(new FixedResponseHandler(() => new StreamContent(new StallingStream())))
        {
            Timeout = TimeSpan.FromMilliseconds(300),
        };
        var storage = new HttpReplayStorage(client, "https://replays.test");

        // HttpClient.Timeout alone covers only the headers once the body is streamed.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.DownloadAsync("https://replays.test/stalled.replay").WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FixedResponseHandler(Func<HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => count;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
