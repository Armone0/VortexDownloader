using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Storage;
using Xunit;

namespace VortexDownloader.Tests;

public class ExpiredLinkTests
{
    private static DownloadTask Make(string name, long total, DownloadStatus status, bool expired = false) => new()
    {
        FileName = name,
        TotalBytes = total,
        Status = status,
        LinkExpired = expired
    };

    [Fact]
    public void Matcher_MatchesNameIgnoringDuplicateSuffixAndCase()
    {
        var target = Make("Movie (1).MP4", 1000, DownloadStatus.Failed, expired: true);
        var tasks = new[] { Make("other.mp4", 1000, DownloadStatus.Failed), target };

        Assert.Same(target, ExpiredLinkMatcher.FindMatch(tasks, "movie.mp4", 1000));
        Assert.Same(target, ExpiredLinkMatcher.FindMatch(tasks, "movie (2).mp4", null));
    }

    [Fact]
    public void Matcher_RejectsSizeMismatch()
    {
        var tasks = new[] { Make("movie.mp4", 1000, DownloadStatus.Failed, expired: true) };
        Assert.Null(ExpiredLinkMatcher.FindMatch(tasks, "movie.mp4", 999));
    }

    [Fact]
    public void Matcher_IgnoresCompletedAndActiveTasks()
    {
        var tasks = new[]
        {
            Make("movie.mp4", 1000, DownloadStatus.Completed),
            Make("movie.mp4", 1000, DownloadStatus.Downloading)
        };
        Assert.Null(ExpiredLinkMatcher.FindMatch(tasks, "movie.mp4", 1000));
    }

    /// <summary>Range-capable fake: 403 for "expired" URLs, serves <c>_data</c> for everything else.</summary>
    private sealed class FakeRangeHandler(byte[] data) : HttpMessageHandler
    {
        public List<string> RangeRequests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            if (url.Contains("expired"))
                return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new ByteArrayContent([]) });

            byte[] body = url.Contains("bigger") ? new byte[data.Length + 10] : data;
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Headers.AcceptRanges.Add("bytes");
                head.Content.Headers.ContentLength = body.Length;
                return System.Threading.Tasks.Task.FromResult(head);
            }

            var range = request.Headers.Range?.Ranges.First();
            long from = range?.From ?? 0, to = Math.Min(range?.To ?? body.Length - 1, body.Length - 1);
            if (range != null) RangeRequests.Add($"{from}-{to}");
            var slice = body.AsSpan((int)from, (int)(to - from + 1)).ToArray();
            var resp = new HttpResponseMessage(range != null ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(slice) };
            if (range != null) resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, body.Length);
            return System.Threading.Tasks.Task.FromResult(resp);
        }
    }

    private static async Task<(DownloadEngine engine, DownloadTask task, FakeRangeHandler handler, string dir, byte[] data)> CreateHalfDoneTaskAsync()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vortex_expired_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        byte[] data = new byte[64 * 1024];
        new Random(7).NextBytes(data);
        var handler = new FakeRangeHandler(data);
        var engine = new DownloadEngine(new StateStore(Path.Combine(dir, "tasks.json")), new HttpClient(handler));

        var failed = new TaskCompletionSource<DownloadTask>();
        engine.TaskFailed += (t, _) => failed.TrySetResult(t);

        var meta = new UrlMetadata("https://cdn.test/expired/file.bin", "file.bin", data.Length, true, null);
        var task = await engine.AddDownloadAsync("https://cdn.test/expired/file.bin", Path.Combine(dir, "file.bin"),
            chunkCount: 1, autoStart: false, preloadedMetadata: meta, sourcePageUrl: "https://site.test/page");

        // Pretend the first half arrived before the link died.
        var chunk = task.Chunks[0];
        File.WriteAllBytes(chunk.TempFilePath, data[..(data.Length / 2)]);
        chunk.CurrentPos = chunk.StartPos + data.Length / 2;
        task.RecalculateDownloadedBytes();

        engine.StartTask(task.Id);
        var done = await System.Threading.Tasks.Task.WhenAny(failed.Task, System.Threading.Tasks.Task.Delay(15000));
        Assert.Same(failed.Task, done);
        return (engine, task, handler, dir, data);
    }

    [Fact]
    public async Task ExpiredLink_FailsAsLinkExpired_AndKeepsChunks()
    {
        var (engine, task, _, dir, data) = await CreateHalfDoneTaskAsync();
        await using (engine)
        {
            Assert.Equal(DownloadStatus.Failed, task.Status);
            Assert.True(task.LinkExpired);
            Assert.Equal(DownloadEngine.LinkExpiredMessage, task.ErrorMessage);
            Assert.Equal(data.Length / 2, task.DownloadedBytes);
            Assert.Equal("https://site.test/page", task.SourcePageUrl);
        }
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task RefreshTaskUrl_RejectsSizeMismatch_AndKeepsOldUrl()
    {
        var (engine, task, _, dir, _) = await CreateHalfDoneTaskAsync();
        await using (engine)
        {
            string? reason = await engine.RefreshTaskUrlAsync(task.Id, "https://cdn.test/bigger/file.bin");
            Assert.NotNull(reason);
            Assert.Equal("https://cdn.test/expired/file.bin", task.Url);
            Assert.True(task.LinkExpired);
            Assert.Equal(DownloadStatus.Failed, task.Status);
        }
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task RefreshTaskUrl_AcceptsMatchingUrl_AndResumesFromDownloadedBytes()
    {
        var (engine, task, handler, dir, data) = await CreateHalfDoneTaskAsync();
        await using (engine)
        {
            var completed = new TaskCompletionSource<bool>();
            engine.TaskCompleted += t => { if (t.Id == task.Id) completed.TrySetResult(true); };
            engine.TaskFailed += (t, e) => { if (t.Id == task.Id) completed.TrySetException(new Exception(e)); };

            string? reason = await engine.RefreshTaskUrlAsync(task.Id, "https://cdn.test/fresh/file.bin", "c=1", "https://site.test/page", null);
            Assert.Null(reason);
            Assert.Equal("https://cdn.test/fresh/file.bin", task.Url);
            Assert.False(task.LinkExpired);

            var done = await System.Threading.Tasks.Task.WhenAny(completed.Task, System.Threading.Tasks.Task.Delay(15000));
            Assert.Same(completed.Task, done);
            await completed.Task;

            // Only the missing second half was requested from the fresh link.
            Assert.Contains($"{data.Length / 2}-{data.Length - 1}", handler.RangeRequests);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(dir, "file.bin")));
        }
        try { Directory.Delete(dir, true); } catch { }
    }
}
