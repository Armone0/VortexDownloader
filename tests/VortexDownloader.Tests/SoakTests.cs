using System.Diagnostics;
using System.IO;
using VortexDownloader.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace VortexDownloader.Tests;

/// <summary>Skipped unless VORTEX_SOAK=1 (scripts/soak-test.ps1 sets it), so a plain `dotnet test` stays fast.</summary>
public sealed class SoakFactAttribute : FactAttribute
{
    public SoakFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("VORTEX_SOAK") != "1")
            Skip = "Soak test: run scripts/soak-test.ps1 or set VORTEX_SOAK=1 and use --filter Category=Soak";
    }
}

public class SoakTests(ITestOutputHelper output)
{
    private static readonly int Downloads = int.TryParse(Environment.GetEnvironmentVariable("VORTEX_SOAK_COUNT"), out var n) ? n : 300;
    private const int Warmup = 60; // lets the thread pool, JIT and HTTP stack reach steady state
    private const long MaxManagedGrowthBytes = 24L * 1024 * 1024;
    private const int MaxHandleGrowth = 30; // a real leak of even 1 handle per download would add 300

    private static (long managed, int handles) Measure()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        using var p = Process.GetCurrentProcess();
        return (GC.GetTotalMemory(true), p.HandleCount);
    }

    [SoakFact]
    [Trait("Category", "Soak")]
    public async Task ManyDownloads_SomeCancelled_DoNotLeakMemoryOrHandles()
    {
        ThreadPool.SetMinThreads(48, 48); // thread-pool growth adds handles that have nothing to do with downloads
        byte[] data = EngineHarness.RandomData(192 * 1024, seed: 3);
        string expected = EngineHarness.Hash(data);

        // "stall" server: every GET sends 8 KB and then hangs until the server is disposed, so cancel always hits a live transfer.
        var stalledSignal = new SemaphoreSlim(0);
        var neverGate = new TaskCompletionSource();
        await using var good = new RawHttpServer(data);
        await using var stall = new RawHttpServer(data, _ => new(CutAfter: 8 * 1024, Stall: neverGate.Task, OnStalled: () => stalledSignal.Release()));
        await using var h = new EngineHarness();
        h.Engine.MaxConcurrentDownloads = 8;
        int[] chunkCounts = [1, 2, 4, 8];
        int failures = 0;

        async Task RunOneAsync(int i)
        {
            string dest = Path.Combine(h.Dir, $"f{i}.bin");
            int chunks = chunkCounts[i % chunkCounts.Length];
            if (i % 5 == 4)
            {
                var t = await h.AddAsync(stall.Url, chunks, dest);
                h.Engine.StartTask(t.Id);
                await stalledSignal.WaitAsync(TimeSpan.FromSeconds(60));
                await h.Engine.RemoveTaskAsync(t.Id); // cancel midway + forget
                return;
            }
            var task = await h.AddAsync(good.Url, chunks, dest);
            string? error = await h.RunToEndAsync(task, timeoutSec: 120);
            if (error != null || EngineHarness.HashFile(dest) != expected) Interlocked.Increment(ref failures);
            await h.Engine.RemoveTaskAsync(task.Id);
            File.Delete(dest);
        }

        async Task RunBatchAsync(int from, int count)
        {
            for (int i = from; i < from + count; i += 10)
                await Task.WhenAll(Enumerable.Range(i, Math.Min(10, from + count - i)).Select(RunOneAsync));
        }

        await RunBatchAsync(0, Warmup);
        var (memBefore, handlesBefore) = Measure();

        var sw = Stopwatch.StartNew();
        for (int third = 0; third < 3; third++)
        {
            await RunBatchAsync(Warmup + third * (Downloads / 3), Downloads / 3);
            var (m, hd) = Measure();
            output.WriteLine($"SOAK after {(third + 1) * (Downloads / 3)}: managed={m / 1024} KB handles={hd} threads={Process.GetCurrentProcess().Threads.Count}");
        }
        sw.Stop();
        var (memAfter, handlesAfter) = Measure();

        long growth = memAfter - memBefore;
        int handleGrowth = handlesAfter - handlesBefore;
        output.WriteLine($"SOAK downloads={Downloads} elapsed={sw.Elapsed.TotalSeconds:F1}s failures={failures}");
        output.WriteLine($"SOAK managed before={memBefore / 1024.0:F0} KB after={memAfter / 1024.0:F0} KB growth={growth / 1024.0:F0} KB (limit {MaxManagedGrowthBytes / 1024} KB)");
        output.WriteLine($"SOAK handles before={handlesBefore} after={handlesAfter} growth={handleGrowth} (limit {MaxHandleGrowth})");

        Assert.Equal(0, failures);
        Assert.Empty(h.Engine.Tasks);
        Assert.True(growth < MaxManagedGrowthBytes, $"managed memory grew by {growth / 1024} KB");
        Assert.True(handleGrowth < MaxHandleGrowth, $"handle count grew by {handleGrowth}");
        neverGate.TrySetResult();
    }
}
