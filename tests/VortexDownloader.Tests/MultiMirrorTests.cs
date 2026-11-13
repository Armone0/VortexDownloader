using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Storage;
using Xunit;

namespace VortexDownloader.Tests;

public class MultiMirrorTests
{
    private const string Primary = "https://primary.test/file.bin";

    /// <summary>
    /// Range-capable fake keyed by host name: "bigger" reports a larger file, "dead" answers every GET with 503,
    /// "flaky" serves half of its first range and then 503, "expired" answers every GET with 403. HEAD always works.
    /// </summary>
    private sealed class MirrorHandler(byte[] data) : HttpMessageHandler
    {
        public ConcurrentDictionary<string, int> RangeGets { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri!.Host;
            byte[] body = host.Contains("bigger") ? new byte[data.Length + 10] : data;

            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                head.Headers.AcceptRanges.Add("bytes");
                head.Content.Headers.ContentLength = body.Length;
                return Task.FromResult(head);
            }

            int nth = RangeGets.AddOrUpdate(host, 1, (_, n) => n + 1);
            if (host.Contains("expired")) return Status(HttpStatusCode.Forbidden);
            if (host.Contains("dead") || (host.Contains("flaky") && nth > 1)) return Status(HttpStatusCode.ServiceUnavailable);

            var range = request.Headers.Range!.Ranges.First();
            long from = range.From ?? 0, to = Math.Min(range.To ?? body.Length - 1, body.Length - 1);
            long sendTo = host.Contains("flaky") ? from + (to - from) / 2 : to;
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body.AsSpan((int)from, (int)(sendTo - from + 1)).ToArray())
            };
            resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, body.Length);
            return Task.FromResult(resp);
        }

        private static Task<HttpResponseMessage> Status(HttpStatusCode code) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new ByteArrayContent([]) });
    }

    private static (DownloadEngine engine, MirrorHandler handler, string dir, byte[] data) Create()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vortex_mirror_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        byte[] data = new byte[256 * 1024];
        new Random(11).NextBytes(data);
        var handler = new MirrorHandler(data);
        return (new DownloadEngine(new StateStore(Path.Combine(dir, "tasks.json")), new HttpClient(handler)), handler, dir, data);
    }

    /// <summary>Adds a 4-connection download and waits until it completes or fails.</summary>
    private static async Task<DownloadTask> RunAsync(DownloadEngine engine, string dir, params string[] mirrors)
    {
        var finished = new TaskCompletionSource<bool>();
        engine.TaskCompleted += _ => finished.TrySetResult(true);
        engine.TaskFailed += (_, _) => finished.TrySetResult(false);

        var task = await engine.AddDownloadAsync(Primary, Path.Combine(dir, "file.bin"), chunkCount: 4, mirrorUrls: mirrors);
        Assert.Same(finished.Task, await Task.WhenAny(finished.Task, Task.Delay(30000)));
        return task;
    }

    [Fact]
    public async Task TwoSources_ServeIdenticalBytes_FileIsCorrect_AndBothAreUsed()
    {
        var (engine, handler, dir, data) = Create();
        await using (engine)
        {
            var task = await RunAsync(engine, dir, "https://mirror.test/file.bin");

            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.Equal(["https://mirror.test/file.bin"], task.MirrorUrls);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(dir, "file.bin")));
            Assert.True(handler.RangeGets.GetValueOrDefault("primary.test") > 0);
            Assert.True(handler.RangeGets.GetValueOrDefault("mirror.test") > 0);
            Assert.Empty(task.DeadSources);
        }
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task MirrorDiesMidway_DownloadCompletesFromTheOtherSource()
    {
        var (engine, handler, dir, data) = Create();
        await using (engine)
        {
            var task = await RunAsync(engine, dir, "https://flaky.test/file.bin");

            Assert.Equal(DownloadStatus.Completed, task.Status);
            Assert.False(task.LinkExpired);
            Assert.Equal(data, await File.ReadAllBytesAsync(Path.Combine(dir, "file.bin")));
            Assert.True(handler.RangeGets.GetValueOrDefault("flaky.test") > 1);
            Assert.Equal(["https://flaky.test/file.bin"], task.DeadSources);
        }
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task AllSourcesDead_FailsWithThePrimarysError()
    {
        var (engine, _, dir, _) = Create();
        await using (engine)
        {
            var finished = new TaskCompletionSource<bool>();
            engine.TaskFailed += (_, _) => finished.TrySetResult(false);
            engine.TaskCompleted += _ => finished.TrySetResult(true);

            var task = await engine.AddDownloadAsync("https://expired.test/file.bin", Path.Combine(dir, "file.bin"),
                chunkCount: 4, mirrorUrls: ["https://dead.test/file.bin"]);
            Assert.Same(finished.Task, await Task.WhenAny(finished.Task, Task.Delay(30000)));

            Assert.Equal(DownloadStatus.Failed, task.Status);
            Assert.True(task.LinkExpired); // primary answered 403; the mirror's 503 does not hide that
            Assert.Equal(2, task.DeadSources.Count);
        }
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task Add_DropsInvalidDuplicateAndMismatchedMirrors_AndPersistsTheRest()
    {
        var (engine, _, dir, _) = Create();
        await using (engine)
        {
            var task = await engine.AddDownloadAsync(Primary, Path.Combine(dir, "file.bin"), chunkCount: 4, autoStart: false,
                mirrorUrls:
                [
                    "https://bigger.test/file.bin",   // different size
                    "ftp://mirror.test/file.bin",     // not http/https
                    "  https://mirror.test/file.bin ",
                    "https://mirror.test/file.bin",   // duplicate
                    Primary                            // same as primary
                ]);

            Assert.Equal(["https://mirror.test/file.bin"], task.MirrorUrls);

            var reloaded = await new StateStore(Path.Combine(dir, "tasks.json")).LoadTasksAsync();
            Assert.Equal(["https://mirror.test/file.bin"], reloaded.Single().MirrorUrls);
        }
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void NormalizeMirrorUrls_CapsAtMaxMirrors()
    {
        var many = Enumerable.Range(0, 20).Select(i => $"https://m{i}.test/file.bin");
        Assert.Equal(DownloadEngine.MaxMirrors, DownloadEngine.NormalizeMirrorUrls(Primary, many).Count);
    }

    [Fact]
    public async Task OldStateFile_WithoutMirrorUrls_StillLoads()
    {
        string file = Path.Combine(Path.GetTempPath(), "vortex_mirror_old_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(file,
                """[{"Id":"7b0e5a0e-3f0b-4b7e-9a51-0d6c6f1b2a11","Url":"https://primary.test/file.bin","FileName":"file.bin","TotalBytes":100,"Chunks":[]}]""");

            var task = (await new StateStore(file).LoadTasksAsync()).Single();

            Assert.Equal("file.bin", task.FileName);
            Assert.Empty(task.MirrorUrls);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }
}
