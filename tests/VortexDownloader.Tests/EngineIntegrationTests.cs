using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Storage;
using Xunit;

namespace VortexDownloader.Tests;

public class EngineIntegrationTests
{
    private class MockRangeHttpHandler : HttpMessageHandler
    {
        private readonly byte[] _data;

        public MockRangeHttpHandler(byte[] data)
        {
            _data = data;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.AcceptRanges.Add("bytes");
                response.Content = new ByteArrayContent([]);
                response.Content.Headers.ContentLength = _data.Length;
                return Task.FromResult(response);
            }

            if (request.Headers.Range != null && request.Headers.Range.Ranges.Count > 0)
            {
                var range = request.Headers.Range.Ranges.First();
                long from = range.From ?? 0;
                long to = range.To ?? (_data.Length - 1);

                if (from >= _data.Length)
                {
                    var notSatisfiable = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        Content = new ByteArrayContent([])
                    };
                    notSatisfiable.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(_data.Length);
                    return Task.FromResult(notSatisfiable);
                }

                to = Math.Min(to, _data.Length - 1);

                int length = (int)(to - from + 1);
                byte[] slice = new byte[length];
                Array.Copy(_data, from, slice, 0, length);

                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice)
                };
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, _data.Length);
                response.Content.Headers.ContentLength = length;
                return Task.FromResult(response);
            }

            // Normal GET
            var fullResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_data)
            };
            fullResponse.Headers.AcceptRanges.Add("bytes");
            fullResponse.Content.Headers.ContentLength = _data.Length;
            return Task.FromResult(fullResponse);
        }
    }

    [Fact]
    public async Task DownloadEngine_DownloadsAndAssemblesFileCorrectly_UsingMultiChunkRange()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "vortex_e2e_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tasksJson = Path.Combine(tempDir, "tasks.json");
        string destFile = Path.Combine(tempDir, "downloaded_data.bin");

        try
        {
            // Generate 1 MB of deterministic pseudo-random bytes
            byte[] originalBytes = new byte[1024 * 1024];
            new Random(42).NextBytes(originalBytes);

            var handler = new MockRangeHttpHandler(originalBytes);
            var httpClient = new HttpClient(handler);
            var stateStore = new StateStore(tasksJson);
            await using var engine = new DownloadEngine(stateStore, httpClient);

            var completionTcs = new TaskCompletionSource<bool>();
            engine.TaskCompleted += task =>
            {
                if (task.DestinationPath == destFile)
                {
                    completionTcs.TrySetResult(true);
                }
            };

            engine.TaskFailed += (task, err) =>
            {
                if (task.DestinationPath == destFile)
                {
                    completionTcs.TrySetException(new Exception($"Download failed: {err}"));
                }
            };

            // Add download with 4 chunks
            var downloadTask = await engine.AddDownloadAsync("https://vortex.test/file.bin", destFile, chunkCount: 4, autoStart: true);

            // Wait up to 10 seconds for completion
            var completed = await Task.WhenAny(completionTcs.Task, Task.Delay(10000));
            Assert.Same(completionTcs.Task, completed);
            Assert.True(await completionTcs.Task);

            // Verify final assembled file exists and matches original byte-for-byte
            Assert.True(File.Exists(destFile));
            byte[] downloadedBytes = await File.ReadAllBytesAsync(destFile);
            Assert.Equal(originalBytes.Length, downloadedBytes.Length);
            Assert.True(originalBytes.SequenceEqual(downloadedBytes));

            // Verify saved state has task as completed
            var savedTasks = await stateStore.LoadTasksAsync();
            var saved = Assert.Single(savedTasks);
            Assert.Equal(DownloadStatus.Completed, saved.Status);
            Assert.Equal(originalBytes.Length, saved.DownloadedBytes);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }
}
