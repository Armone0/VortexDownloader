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

public class QueueManagerTests
{
    private class ControllableHttpHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _blockers = new();

        public void Block(string url)
        {
            _blockers.TryAdd(url, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        public void Release(string url)
        {
            if (_blockers.TryGetValue(url, out var tcs))
            {
                tcs.TrySetResult(true);
            }
        }

        public void ReleaseAll()
        {
            foreach (var tcs in _blockers.Values)
            {
                tcs.TrySetResult(true);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? string.Empty;

            if (request.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.AcceptRanges.Add("bytes");
                response.Content = new ByteArrayContent([]);
                response.Content.Headers.ContentLength = 1000;
                return response;
            }

            if (_blockers.TryGetValue(url, out var tcs))
            {
                using (cancellationToken.Register(() => tcs.TrySetCanceled()))
                {
                    try
                    {
                        await tcs.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        var canceledResponse = new HttpResponseMessage(HttpStatusCode.RequestTimeout);
                        return canceledResponse;
                    }
                }
            }

            long from = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            long to = request.Headers.Range?.Ranges.FirstOrDefault()?.To ?? 999;
            to = Math.Min(to, 999);
            int length = (int)(to - from + 1);
            byte[] slice = new byte[length];

            var partResponse = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(slice)
            };
            partResponse.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, 1000);
            partResponse.Content.Headers.ContentLength = length;
            return partResponse;
        }
    }

    [Fact]
    public async Task MaxConcurrentDownloads_LimitsActiveDownloads_AndProcessesQueueOnCompletion()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vortex_queue_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var handler = new ControllableHttpHandler();
        handler.Block("https://vortex.test/file1.bin");
        handler.Block("https://vortex.test/file2.bin");
        handler.Block("https://vortex.test/file3.bin");
        handler.Block("https://vortex.test/file4.bin");

        using var httpClient = new HttpClient(handler);
        var stateStore = new StateStore(Path.Combine(tempDir, "tasks.json"));
        await using var engine = new DownloadEngine(stateStore, httpClient);
        engine.MaxConcurrentDownloads = 2;

        try
        {
            var task1 = await engine.AddDownloadAsync("https://vortex.test/file1.bin", Path.Combine(tempDir, "f1.bin"), chunkCount: 1, autoStart: true);
            var task2 = await engine.AddDownloadAsync("https://vortex.test/file2.bin", Path.Combine(tempDir, "f2.bin"), chunkCount: 1, autoStart: true);
            var task3 = await engine.AddDownloadAsync("https://vortex.test/file3.bin", Path.Combine(tempDir, "f3.bin"), chunkCount: 1, autoStart: true);
            var task4 = await engine.AddDownloadAsync("https://vortex.test/file4.bin", Path.Combine(tempDir, "f4.bin"), chunkCount: 1, autoStart: true);

            // Wait for workers to spin up
            await Task.Delay(250);

            // Exactly 2 downloading, 2 queued
            Assert.Equal(DownloadStatus.Downloading, task1.Status);
            Assert.Equal(DownloadStatus.Downloading, task2.Status);
            Assert.Equal(DownloadStatus.Queued, task3.Status);
            Assert.Equal(DownloadStatus.Queued, task4.Status);

            // Release file1 and wait for task1 to complete
            var tcsTask1 = new TaskCompletionSource<bool>();
            engine.TaskCompleted += t =>
            {
                if (t.Id == task1.Id) tcsTask1.TrySetResult(true);
            };

            handler.Release("https://vortex.test/file1.bin");
            await Task.WhenAny(tcsTask1.Task, Task.Delay(5000));

            // Wait for queue processor to promote next task
            await Task.Delay(250);

            Assert.Equal(DownloadStatus.Completed, task1.Status);
            // Task3 should now be Downloading, Task4 should still be Queued
            Assert.Equal(DownloadStatus.Downloading, task3.Status);
            Assert.Equal(DownloadStatus.Queued, task4.Status);

            // Release all remaining
            handler.ReleaseAll();
            await Task.Delay(500);
        }
        finally
        {
            handler.ReleaseAll();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task StartTask_ExplicitCall_StartsTaskEvenWhenConcurrencyLimitReached()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vortex_queue_explicit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var handler = new ControllableHttpHandler();
        handler.Block("https://vortex.test/f1.bin");
        handler.Block("https://vortex.test/f2.bin");

        using var httpClient = new HttpClient(handler);
        var stateStore = new StateStore(Path.Combine(tempDir, "tasks.json"));
        await using var engine = new DownloadEngine(stateStore, httpClient);
        engine.MaxConcurrentDownloads = 1;

        try
        {
            var task1 = await engine.AddDownloadAsync("https://vortex.test/f1.bin", Path.Combine(tempDir, "f1.bin"), chunkCount: 1, autoStart: true);
            var task2 = await engine.AddDownloadAsync("https://vortex.test/f2.bin", Path.Combine(tempDir, "f2.bin"), chunkCount: 1, autoStart: true);

            await Task.Delay(200);

            Assert.Equal(DownloadStatus.Downloading, task1.Status);
            Assert.Equal(DownloadStatus.Queued, task2.Status);

            // Explicit start should override queue limit
            engine.StartTask(task2.Id);
            await Task.Delay(200);

            Assert.Equal(DownloadStatus.Downloading, task2.Status);
        }
        finally
        {
            handler.ReleaseAll();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task PauseTaskAsync_FreesSlot_AndStartsNextQueuedTask()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vortex_queue_pause_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var handler = new ControllableHttpHandler();
        handler.Block("https://vortex.test/p1.bin");
        handler.Block("https://vortex.test/p2.bin");

        using var httpClient = new HttpClient(handler);
        var stateStore = new StateStore(Path.Combine(tempDir, "tasks.json"));
        await using var engine = new DownloadEngine(stateStore, httpClient);
        engine.MaxConcurrentDownloads = 1;

        try
        {
            var task1 = await engine.AddDownloadAsync("https://vortex.test/p1.bin", Path.Combine(tempDir, "p1.bin"), chunkCount: 1, autoStart: true);
            var task2 = await engine.AddDownloadAsync("https://vortex.test/p2.bin", Path.Combine(tempDir, "p2.bin"), chunkCount: 1, autoStart: true);

            await Task.Delay(200);

            Assert.Equal(DownloadStatus.Downloading, task1.Status);
            Assert.Equal(DownloadStatus.Queued, task2.Status);

            // Pause active task
            await engine.PauseTaskAsync(task1.Id);
            await Task.Delay(250);

            Assert.Equal(DownloadStatus.Paused, task1.Status);
            // Queued task should now be auto-promoted to Downloading
            Assert.Equal(DownloadStatus.Downloading, task2.Status);
        }
        finally
        {
            handler.ReleaseAll();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task IncreasingMaxConcurrentDownloads_StartsWaitingQueuedTasks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vortex_queue_increase_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var handler = new ControllableHttpHandler();
        handler.Block("https://vortex.test/inc1.bin");
        handler.Block("https://vortex.test/inc2.bin");
        handler.Block("https://vortex.test/inc3.bin");

        using var httpClient = new HttpClient(handler);
        var stateStore = new StateStore(Path.Combine(tempDir, "tasks.json"));
        await using var engine = new DownloadEngine(stateStore, httpClient);
        engine.MaxConcurrentDownloads = 1;

        try
        {
            var task1 = await engine.AddDownloadAsync("https://vortex.test/inc1.bin", Path.Combine(tempDir, "inc1.bin"), chunkCount: 1, autoStart: true);
            var task2 = await engine.AddDownloadAsync("https://vortex.test/inc2.bin", Path.Combine(tempDir, "inc2.bin"), chunkCount: 1, autoStart: true);
            var task3 = await engine.AddDownloadAsync("https://vortex.test/inc3.bin", Path.Combine(tempDir, "inc3.bin"), chunkCount: 1, autoStart: true);

            await Task.Delay(200);

            Assert.Equal(DownloadStatus.Downloading, task1.Status);
            Assert.Equal(DownloadStatus.Queued, task2.Status);
            Assert.Equal(DownloadStatus.Queued, task3.Status);

            // Dynamically increase concurrency capacity
            engine.MaxConcurrentDownloads = 3;
            await Task.Delay(250);

            // All tasks should now be Downloading
            Assert.Equal(DownloadStatus.Downloading, task1.Status);
            Assert.Equal(DownloadStatus.Downloading, task2.Status);
            Assert.Equal(DownloadStatus.Downloading, task3.Status);
        }
        finally
        {
            handler.ReleaseAll();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public async Task MaxConcurrentDownloads_WhenSetToZero_AllowsUnlimitedConcurrentDownloads()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vortex_queue_unlimited_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var handler = new ControllableHttpHandler();
        handler.Block("https://vortex.test/unl1.bin");
        handler.Block("https://vortex.test/unl2.bin");
        handler.Block("https://vortex.test/unl3.bin");
        handler.Block("https://vortex.test/unl4.bin");

        using var httpClient = new HttpClient(handler);
        var stateStore = new StateStore(Path.Combine(tempDir, "tasks.json"));
        await using var engine = new DownloadEngine(stateStore, httpClient);
        engine.MaxConcurrentDownloads = 0; // Unlimited

        try
        {
            var task1 = await engine.AddDownloadAsync("https://vortex.test/unl1.bin", Path.Combine(tempDir, "u1.bin"), chunkCount: 1, autoStart: true);
            var task2 = await engine.AddDownloadAsync("https://vortex.test/unl2.bin", Path.Combine(tempDir, "u2.bin"), chunkCount: 1, autoStart: true);
            var task3 = await engine.AddDownloadAsync("https://vortex.test/unl3.bin", Path.Combine(tempDir, "u3.bin"), chunkCount: 1, autoStart: true);
            var task4 = await engine.AddDownloadAsync("https://vortex.test/unl4.bin", Path.Combine(tempDir, "u4.bin"), chunkCount: 1, autoStart: true);

            await Task.Delay(250);

            // All 4 should be downloading concurrently
            Assert.Equal(DownloadStatus.Downloading, task1.Status);
            Assert.Equal(DownloadStatus.Downloading, task2.Status);
            Assert.Equal(DownloadStatus.Downloading, task3.Status);
            Assert.Equal(DownloadStatus.Downloading, task4.Status);
        }
        finally
        {
            handler.ReleaseAll();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
