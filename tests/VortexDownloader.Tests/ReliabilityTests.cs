using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Storage;
using Xunit;

namespace VortexDownloader.Tests;

/// <summary>Shared plumbing for tests that run the real engine against <see cref="RawHttpServer"/>.</summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "vortex_rel_" + Guid.NewGuid().ToString("N"));
    public DownloadEngine Engine { get; }
    public string Dest => Path.Combine(Dir, "out.bin");
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string?>> _done = new();

    public EngineHarness()
    {
        Directory.CreateDirectory(Dir);
        Engine = new DownloadEngine(new StateStore(Path.Combine(Dir, "tasks.json")), new HttpClient()) { ScanWithDefender = false };
        Engine.TaskCompleted += t => Slot(t.Id).TrySetResult(null);
        Engine.TaskFailed += (t, err) => Slot(t.Id).TrySetResult(err);
    }

    private TaskCompletionSource<string?> Slot(Guid id) => _done.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Adds a task without starting it, so the caller can wire up waits before any event fires.</summary>
    public Task<DownloadTask> AddAsync(string url, int chunks, string? dest = null) =>
        Engine.AddDownloadAsync(url, dest ?? Dest, chunkCount: chunks, autoStart: false);

    /// <summary>Starts (or resumes) the task and waits for its next terminal event. Returns null on completion, the error text on failure.</summary>
    public async Task<string?> RunToEndAsync(DownloadTask task, bool resume = false, int timeoutSec = 60)
    {
        var slot = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _done[task.Id] = slot; // armed before the start so an instant finish cannot be missed
        if (resume) await Engine.ResumeTaskAsync(task.Id);
        else Engine.StartTask(task.Id);
        return await slot.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSec));
    }

    public static byte[] RandomData(int size, int seed = 7)
    {
        byte[] data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    public static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        try { Directory.Delete(Dir, true); } catch { }
    }
}

public class ReliabilityTests
{
    private static readonly byte[] Data = EngineHarness.RandomData(1024 * 1024);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutSec = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(20);
        }
    }

    private static async Task<List<Exception>> UnobservedAfterGcAsync(Func<Task> body)
    {
        var seen = new ConcurrentBag<Exception>();
        void Handler(object? s, UnobservedTaskExceptionEventArgs e) => seen.Add(e.Exception);
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            await body();
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(50);
            }
        }
        finally { TaskScheduler.UnobservedTaskException -= Handler; }
        return [.. seen];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ConnectionCutMidBody_RetriesResumesAndHashMatches(int chunks)
    {
        // The first three GETs deliver a fraction of their body and then drop the socket.
        await using var server = new RawHttpServer(Data, r => r.GetNumber <= 3 ? new(CutAfter: 50_000, CloseAfterCut: true) : new());
        await using var h = new EngineHarness();

        var task = await h.AddAsync(server.Url, chunks);
        Assert.Null(await h.RunToEndAsync(task));

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(EngineHarness.Hash(Data), EngineHarness.HashFile(h.Dest));
        Assert.True(server.GetCount > chunks, "at least one chunk must have been re-requested");
    }

    [Fact]
    public async Task ServerStalls_PauseReturnsPromptly_ResumeFinishesWithMatchingHash()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new RawHttpServer(Data, r => r.GetNumber == 1
            ? new(CutAfter: 200_000, Stall: gate.Task, OnStalled: () => stalled.TrySetResult())
            : new());
        await using var h = new EngineHarness();

        var task = await h.AddAsync(server.Url, 1);
        h.Engine.StartTask(task.Id);
        await stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => task.Chunks[0].CurrentPos >= 200_000);

        await h.Engine.PauseTaskAsync(task.Id).WaitAsync(TimeSpan.FromSeconds(15)); // a stuck read must not block pause
        Assert.Equal(DownloadStatus.Paused, task.Status);
        gate.TrySetResult();

        Assert.Null(await h.RunToEndAsync(task, resume: true));
        Assert.Equal(EngineHarness.Hash(Data), EngineHarness.HashFile(h.Dest));
    }

    [Fact]
    public async Task ServerStartsReturning500_TaskFailsClearly_NoFinalFile()
    {
        await using var server = new RawHttpServer(Data, r => r.GetNumber == 1 ? new(CutAfter: 300_000, CloseAfterCut: true) : new(Status: 500));
        await using var h = new EngineHarness();

        var task = await h.AddAsync(server.Url, 1);
        string? error = await h.RunToEndAsync(task);

        Assert.NotNull(error);
        Assert.Contains("500", error);
        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Contains("500", task.ErrorMessage);
        Assert.False(File.Exists(h.Dest), "a failed download must not leave a final file");
        Assert.True(task.Chunks[0].DownloadedBytes >= 300_000, "partial data stays for resume");
    }

    [Fact]
    public async Task WriteIoError_FailsCleanly_KeepsPartialData_ReleasesHandles_NoUnobservedException()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new RawHttpServer(Data, r => r.GetNumber == 1
            ? new(CutAfter: 200_000, Stall: gate.Task, OnStalled: () => stalled.TrySetResult())
            : new());
        await using var h = new EngineHarness();
        DownloadTask task = null!;

        var unobserved = await UnobservedAfterGcAsync(async () =>
        {
            task = await h.AddAsync(server.Url, 1);
            h.Engine.StartTask(task.Id);
            await stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(() => task.Chunks[0].CurrentPos >= 200_000);
            await h.Engine.PauseTaskAsync(task.Id);

            string part = task.Chunks[0].TempFilePath;
            long partialBytes = task.Chunks[0].DownloadedBytes;
            Assert.True(partialBytes >= 200_000);

            // Pausing must have released the part file; taking an exclusive lock now proves it,
            // and holding it makes the next write attempt fail with an IOException (sharing violation) like a broken disk would.
            var blocker = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            try
            {
                string? error = await h.RunToEndAsync(task, resume: true);
                Assert.NotNull(error);
                Assert.Equal(DownloadStatus.Failed, task.Status);
                Assert.False(string.IsNullOrWhiteSpace(task.ErrorMessage));
            }
            finally { blocker.Dispose(); }

            // Failed task: partial data intact and no handle left behind (an exclusive open succeeds and delete is possible).
            Assert.Equal(partialBytes, task.Chunks[0].DownloadedBytes);
            using (new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Assert.Equal(partialBytes, new FileInfo(part).Length);
            Assert.False(File.Exists(h.Dest));

            // Disk is fine again: the same task resumes and the result is byte-exact.
            Assert.Null(await h.RunToEndAsync(task, resume: true));
            Assert.Equal(EngineHarness.Hash(Data), EngineHarness.HashFile(h.Dest));
            gate.TrySetResult();
        });

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task PartFileCannotBeCreated_FailsCleanly_NoUnobservedException()
    {
        await using var server = new RawHttpServer(Data);
        await using var h = new EngineHarness();

        var unobserved = await UnobservedAfterGcAsync(async () =>
        {
            var task = await h.AddAsync(server.Url, 4);
            // A directory squatting on a part file path makes every open fail (UnauthorizedAccessException).
            Directory.CreateDirectory(task.Chunks[1].TempFilePath);

            string? error = await h.RunToEndAsync(task);
            Assert.NotNull(error);
            Assert.Equal(DownloadStatus.Failed, task.Status);
            Assert.False(File.Exists(h.Dest));

            // Handles of the chunks that did download must be free: their part files can be deleted right away.
            foreach (var chunk in task.Chunks.Where(c => File.Exists(c.TempFilePath)))
                File.Delete(chunk.TempFilePath);
        });

        Assert.Empty(unobserved);
    }

    [Fact]
    public async Task ServerStallsForever_ChunkReconnectsOnItsOwn()
    {
        var never = new TaskCompletionSource(); // never released: only the stall timeout can unstick the read
        await using var server = new RawHttpServer(Data, r => r.GetNumber == 1
            ? new(CutAfter: 200_000, Stall: never.Task)
            : new());
        await using var h = new EngineHarness();
        var old = VortexDownloader.Core.Network.ChunkDownloader.StallTimeout;
        VortexDownloader.Core.Network.ChunkDownloader.StallTimeout = TimeSpan.FromSeconds(1);
        try
        {
            var task = await h.AddAsync(server.Url, 1);
            Assert.Null(await h.RunToEndAsync(task));
            Assert.Equal(EngineHarness.Hash(Data), EngineHarness.HashFile(h.Dest));
        }
        finally
        {
            VortexDownloader.Core.Network.ChunkDownloader.StallTimeout = old;
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070), true)]  // disk full
    [InlineData(unchecked((int)0x80070020), true)]  // sharing violation
    [InlineData(unchecked((int)0x80004005), false)] // generic (e.g. connection reset while reading)
    public void LocalDiskErrors_DoNotCountAsDeadMirror(int hresult, bool local)
    {
        Assert.Equal(local, DownloadEngine.IsLocalDiskError(new IOException("x", hresult)));
        Assert.False(DownloadEngine.IsLocalDiskError(new HttpRequestException("x")));
    }
}
