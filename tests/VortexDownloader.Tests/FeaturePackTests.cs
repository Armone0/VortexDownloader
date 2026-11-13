using System.IO;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Media;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Storage;
using Xunit;

namespace VortexDownloader.Tests;

/// <summary>Scheduler, expected hash, auto-folder, monthly stats and ffmpeg presets.</summary>
public class FeaturePackTests
{
    // ---------- Scheduler ----------

    [Fact]
    public void Schedule_FutureIsWaiting_PastIsDue()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var future = new DownloadTask { Status = DownloadStatus.Queued, StartAt = now.AddMinutes(5) };
        var past = new DownloadTask { Status = DownloadStatus.Queued, StartAt = now.AddHours(-3) };
        var none = new DownloadTask { Status = DownloadStatus.Queued };
        var paused = new DownloadTask { Status = DownloadStatus.Paused, StartAt = now.AddHours(-1) };

        Assert.True(DownloadSchedule.IsWaiting(future, now));
        Assert.False(DownloadSchedule.IsDue(future, now));
        Assert.True(DownloadSchedule.IsDue(past, now));
        Assert.False(DownloadSchedule.IsWaiting(past, now));
        Assert.False(DownloadSchedule.IsWaiting(none, now));
        Assert.False(DownloadSchedule.IsDue(none, now));
        Assert.False(DownloadSchedule.IsDue(paused, now));
    }

    [Theory]
    [InlineData(23, 0, 2, 0, 1)]   // 02:00 picked at 23:00 -> tomorrow
    [InlineData(1, 0, 2, 0, 0)]    // later today
    [InlineData(10, 30, 10, 30, 0)] // current minute -> today (starts at once)
    [InlineData(10, 31, 10, 30, 1)] // minute already over -> tomorrow
    public void Schedule_NextOccurrence(int nowH, int nowM, int h, int m, int addDays)
    {
        var now = new DateTime(2026, 11, 13, nowH, nowM, 20);
        var result = DownloadSchedule.NextOccurrence(now, h, m);
        Assert.Equal(now.Date.AddDays(addDays).AddHours(h).AddMinutes(m), result);
    }

    [Theory]
    [InlineData(2, 0, "de")]
    [InlineData(3, 0, "te")]
    [InlineData(6, 0, "da")]
    [InlineData(0, 0, "da")]
    [InlineData(2, 30, "da")]
    [InlineData(2, 40, "ta")]
    [InlineData(12, 0, "de")]
    [InlineData(9, 15, "te")]
    public void Schedule_TurkishSuffix(int h, int m, string expected) =>
        Assert.Equal(expected, DownloadSchedule.TimeSuffix(h, m));

    [Fact]
    public void Schedule_Label()
    {
        var now = new DateTime(2026, 11, 13, 1, 0, 0, DateTimeKind.Local);
        var today = new DateTime(2026, 11, 13, 2, 0, 0, DateTimeKind.Local).ToUniversalTime();
        var tomorrow = new DateTime(2026, 11, 14, 3, 0, 0, DateTimeKind.Local).ToUniversalTime();
        Assert.Equal("⏰ 02:00'de başlayacak", DownloadSchedule.FormatLabel(today, now));
        Assert.Equal("⏰ yarın 03:00'te başlayacak", DownloadSchedule.FormatLabel(tomorrow, now));
    }

    [Fact]
    public async Task Schedule_FutureTaskIsNotStartedByQueue_PauseClearsSchedule()
    {
        string state = Path.Combine(Path.GetTempPath(), "vortex_sched_" + Guid.NewGuid().ToString("N") + ".json");
        await using var engine = new DownloadEngine(new StateStore(state));
        try
        {
            var meta = new UrlMetadata("https://example.invalid/a.bin", "a.bin", 10, false, null);
            var task = await engine.AddDownloadAsync("https://example.invalid/a.bin", Path.Combine(Path.GetTempPath(), "a.bin"),
                1, autoStart: true, preloadedMetadata: meta, startAt: DateTime.UtcNow.AddHours(5));

            Assert.Equal(DownloadStatus.Queued, task.Status);
            Assert.NotNull(task.StartAt);
            await Task.Delay(700); // one monitor tick
            Assert.Equal(DownloadStatus.Queued, task.Status);

            await engine.PauseTaskAsync(task.Id);
            Assert.Equal(DownloadStatus.Paused, task.Status);
            Assert.Null(task.StartAt);
        }
        finally
        {
            try { File.Delete(state); } catch { }
        }
    }

    [Fact]
    public async Task StateStore_PersistsNewFields()
    {
        string state = Path.Combine(Path.GetTempPath(), "vortex_fields_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new StateStore(state);
            var at = new DateTime(2026, 11, 14, 1, 0, 0, DateTimeKind.Utc);
            await store.SaveTasksAsync([new DownloadTask { StartAt = at, ExpectedSha256 = new string('a', 64), ActiveSeconds = 12.5 }]);
            var loaded = (await store.LoadTasksAsync()).Single();
            Assert.Equal(at, loaded.StartAt!.Value.ToUniversalTime());
            Assert.Equal(new string('a', 64), loaded.ExpectedSha256);
            Assert.Equal(12.5, loaded.ActiveSeconds);
        }
        finally
        {
            try { File.Delete(state); } catch { }
        }
    }

    // ---------- Expected hash ----------

    [Fact]
    public void Hash_Normalize_AcceptsSpacesAndCase()
    {
        string hex = "AB" + new string('c', 62);
        Assert.True(FileHasher.TryNormalizeSha256($"  {hex[..32]} {hex[32..]}\n", out var n));
        Assert.Equal(hex.ToLowerInvariant(), n);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("00000000000000000000000000000000000000000000000000000000000000000")] // 65
    public void Hash_Normalize_RejectsInvalid(string input) =>
        Assert.False(FileHasher.TryNormalizeSha256(input, out _));

    [Fact]
    public void Hash_Normalize_EmptyMeansNoExpectation()
    {
        Assert.True(FileHasher.TryNormalizeSha256("   ", out var n));
        Assert.Null(n);
    }

    [Fact]
    public void Hash_Compare()
    {
        string h = new('a', 64);
        Assert.True(FileHasher.Matches(h, h.ToUpperInvariant()));
        Assert.False(FileHasher.Matches(h, new string('b', 64)));
        Assert.False(FileHasher.Matches(h, null));
    }

    // ---------- Category folders ----------

    [Theory]
    [InlineData("film.MKV", "Video")]
    [InlineData("song.mp3", "Müzik")]
    [InlineData("doc.pdf", "Belgeler")]
    [InlineData("pack.7z", "Arşivler")]
    [InlineData("setup.exe", "Programlar")]
    [InlineData("data.bin", "Diğer")]
    [InlineData("noext", "Diğer")]
    public void Category_FolderName(string file, string folder) =>
        Assert.Equal(folder, FileCategories.FolderName(FileCategories.GetCategory(file)));

    [Fact]
    public void AutoFolder_OnlyForDefaultRoot_AndUnique()
    {
        string root = Path.Combine(Path.GetTempPath(), "vortex_root_" + Guid.NewGuid().ToString("N"));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(root, "Video", "a.mp4") };

        Assert.Equal(Path.Combine(root, "Video", "a (1).mp4"),
            FileCategories.ResolveAutoFolderPath(Path.Combine(root, "a.mp4"), root + Path.DirectorySeparatorChar, taken.Contains));
        Assert.Equal(Path.Combine(root, "Diğer", "x.bin"),
            FileCategories.ResolveAutoFolderPath(Path.Combine(root, "x.bin"), root, taken.Contains));

        string other = Path.Combine(root, "elsewhere", "a.mp4");
        Assert.Equal(other, FileCategories.ResolveAutoFolderPath(other, root, taken.Contains));
        Assert.Equal(other, FileCategories.ResolveAutoFolderPath(other, null, taken.Contains));
    }

    [Fact]
    public void UniqueFilePath_SkipsTakenNames()
    {
        var taken = new HashSet<string> { @"C:\d\f.txt", @"C:\d\f (1).txt" };
        Assert.Equal(@"C:\d\f (2).txt", FileCategories.UniqueFilePath(@"C:\d\f.txt", taken.Contains));
        Assert.Equal(@"C:\d\g.txt", FileCategories.UniqueFilePath(@"C:\d\g.txt", taken.Contains));
    }

    // ---------- Monthly stats ----------

    [Fact]
    public void Stats_ThisMonthOnly_AverageFromActiveTime()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Local);
        const long MB = 1024 * 1024;
        var tasks = new[]
        {
            new DownloadTask { Status = DownloadStatus.Completed, CompletedAt = now.AddDays(-1).ToUniversalTime(), TotalBytes = 100 * MB, ActiveSeconds = 10 },
            new DownloadTask { Status = DownloadStatus.Completed, CompletedAt = now.AddDays(-2).ToUniversalTime(), TotalBytes = 100 * MB, ActiveSeconds = 0 }, // untimed
            new DownloadTask { Status = DownloadStatus.Completed, CompletedAt = now.AddMonths(-1).ToUniversalTime(), TotalBytes = 999 * MB, ActiveSeconds = 1 },
            new DownloadTask { Status = DownloadStatus.Paused, TotalBytes = 5 * MB },
        };

        var s = DownloadStats.ForMonth(tasks, now);
        Assert.Equal(2, s.Count);
        Assert.Equal(200 * MB, s.TotalBytes);
        Assert.Equal(10.0 * MB, s.AverageBytesPerSec, 3);
        Assert.Equal("Bu ay: 200,0 MB · 2 indirme · ortalama 10,0 MB/s", DownloadStats.Format(s));
    }

    [Fact]
    public void Stats_Format_GigabytesAndNoAverage()
    {
        var s = new MonthlyDownloadStats((long)(42.3 * 1024 * 1024 * 1024), 128, 0);
        Assert.Equal("Bu ay: 42,3 GB · 128 indirme", DownloadStats.Format(s));
    }

    // ---------- ffmpeg presets ----------

    [Fact]
    public void Ffmpeg_Arguments()
    {
        Assert.Equal(["-hide_banner", "-nostdin", "-n", "-i", "in.mkv", "-c", "copy", "-progress", "pipe:1", "-nostats", "out.mp4"],
            FfmpegPresets.BuildArguments(FfmpegPreset.RemuxToMp4, "in.mkv", "out.mp4"));
        Assert.Equal(["-hide_banner", "-nostdin", "-n", "-i", "in.mkv", "-vn", "-c:a", "copy", "-progress", "pipe:1", "-nostats", "out.m4a"],
            FfmpegPresets.BuildArguments(FfmpegPreset.ExtractAudioM4a, "in.mkv", "out.m4a"));
        Assert.Contains("aac", FfmpegPresets.BuildArguments(FfmpegPreset.ExtractAudioM4a, "in.mkv", "out.m4a", reencodeAudio: true));
    }

    [Fact]
    public void Ffmpeg_OutputPath_NeverSource()
    {
        Assert.Equal(@"C:\v\a.mp4", FfmpegPresets.OutputPathFor(FfmpegPreset.RemuxToMp4, @"C:\v\a.mkv", _ => false));
        Assert.Equal(@"C:\v\a (1).mp4", FfmpegPresets.OutputPathFor(FfmpegPreset.RemuxToMp4, @"C:\v\a.mp4", _ => false));
        Assert.Equal(@"C:\v\a (1).m4a", FfmpegPresets.OutputPathFor(FfmpegPreset.ExtractAudioM4a, @"C:\v\a.webm", p => p == @"C:\v\a.m4a"));
    }

    [Fact]
    public void Ffmpeg_ParseProgress()
    {
        Assert.True(FfmpegPresets.TryParseDuration("  Duration: 00:01:02.50, start: 0.000000, bitrate: 1 kb/s", out var d));
        Assert.Equal(TimeSpan.FromSeconds(62.5), d);
        Assert.True(FfmpegPresets.TryParseOutTime("out_time_us=31250000", out var p));
        Assert.Equal(TimeSpan.FromSeconds(31.25), p);
        Assert.False(FfmpegPresets.TryParseOutTime("out_time=00:00:31.250000", out _));
    }

    private sealed class ScriptedRunner(params int[] exitCodes) : IProcessRunner
    {
        public List<List<string>> Calls { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(string fileName, string arguments,
            Action<string>? onOutputLine = null, Action<string>? onErrorLine = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("ffmpeg must use the argument-list overload.");

        public Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> argumentList,
            Action<string>? onOutputLine = null, Action<string>? onErrorLine = null, CancellationToken ct = default)
        {
            var args = argumentList.ToList();
            Calls.Add(args);
            onErrorLine?.Invoke("  Duration: 00:00:10.00, start: 0");
            onOutputLine?.Invoke("out_time_us=5000000");
            File.WriteAllText(args[^1], "partial"); // ffmpeg creates the output even when it then fails
            int code = exitCodes[Calls.Count - 1];
            return Task.FromResult(new ProcessExecutionResult(code, "", code == 0 ? "" : "error line"));
        }
    }

    [Fact]
    public async Task Ffmpeg_AudioCopyFails_FallsBackToAac()
    {
        string dir = Directory.CreateTempSubdirectory("vortex_ff_").FullName;
        try
        {
            string src = Path.Combine(dir, "clip.webm");
            File.WriteAllText(src, "source");
            var runner = new ScriptedRunner(1, 0);
            var progress = new List<double>();

            string output = await FfmpegPresets.ConvertAsync(runner, "ffmpeg.exe", FfmpegPreset.ExtractAudioM4a, src, progress.Add);

            Assert.Equal(Path.Combine(dir, "clip.m4a"), output);
            Assert.Equal(2, runner.Calls.Count);
            Assert.Contains("copy", runner.Calls[0]);
            Assert.Contains("aac", runner.Calls[1]);
            Assert.Contains(50.0, progress);
            Assert.Equal("source", File.ReadAllText(src));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Ffmpeg_RemuxFails_ClearMessage_PartialDeleted()
    {
        string dir = Directory.CreateTempSubdirectory("vortex_ff_").FullName;
        try
        {
            string src = Path.Combine(dir, "clip.mkv");
            File.WriteAllText(src, "source");
            var runner = new ScriptedRunner(1);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                FfmpegPresets.ConvertAsync(runner, "ffmpeg.exe", FfmpegPreset.RemuxToMp4, src));

            Assert.Equal(FfmpegPresets.RemuxIncompatibleMessage, ex.Message);
            Assert.Single(runner.Calls);
            Assert.False(File.Exists(Path.Combine(dir, "clip.mp4")));
            Assert.True(File.Exists(src));
        }
        finally { Directory.Delete(dir, true); }
    }
}
