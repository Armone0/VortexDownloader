using System.IO;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Storage;
using Xunit;

namespace VortexDownloader.Tests;

/// <summary>Crash restore: whatever a power cut leaves in tasks.json, startup must not throw.</summary>
public class StateStoreCrashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vortex_crash_" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "tasks.json");

    public StateStoreCrashTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static DownloadTask Task_(string name) => new() { Url = "https://x.test/" + name, FileName = name, Status = DownloadStatus.Paused };

    [Fact]
    public async Task EmptyFile_LoadsAsEmpty()
    {
        await File.WriteAllBytesAsync(File_, []);
        Assert.Empty(await new StateStore(File_).LoadTasksAsync());
    }

    [Fact]
    public async Task NullBytesFile_LoadsAsEmpty()
    {
        // A common post-crash NTFS state: the right length, all zeros.
        await File.WriteAllBytesAsync(File_, new byte[512]);
        Assert.Empty(await new StateStore(File_).LoadTasksAsync());
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.99)]
    public async Task HalfWrittenFile_LoadsAsEmpty_KeepsBadFileAside_AndNextSaveWorks(double fraction)
    {
        var store = new StateStore(File_);
        await store.SaveTasksAsync([Task_("a"), Task_("b")]);
        byte[] full = await File.ReadAllBytesAsync(File_);
        await File.WriteAllBytesAsync(File_, full[..(int)(full.Length * fraction)]);

        Assert.Empty(await store.LoadTasksAsync());
        Assert.True(File.Exists(File_ + ".corrupt"));

        await store.SaveTasksAsync([Task_("c")]);
        Assert.Equal("c", Assert.Single(await store.LoadTasksAsync()).FileName);
    }

    [Fact]
    public async Task NullEntryInList_SkipsItAndKeepsTheRest()
    {
        var store = new StateStore(File_);
        await store.SaveTasksAsync([Task_("a")]);
        string json = await File.ReadAllTextAsync(File_);
        await File.WriteAllTextAsync(File_, "[null," + json.TrimStart()[1..]);

        Assert.Equal("a", Assert.Single(await store.LoadTasksAsync()).FileName);
    }

    [Fact]
    public async Task CrashDuringSave_LeavesTornTmpFile_OldStateStillLoads_NextSaveReplacesIt()
    {
        var store = new StateStore(File_);
        await store.SaveTasksAsync([Task_("old")]);
        // The writer died halfway through the temp file, before the rename.
        await File.WriteAllTextAsync(File_ + ".tmp", "[{\"id\":\"");

        Assert.Equal("old", Assert.Single(await store.LoadTasksAsync()).FileName);

        await store.SaveTasksAsync([Task_("new")]);
        Assert.Equal("new", Assert.Single(await store.LoadTasksAsync()).FileName);
        Assert.False(File.Exists(File_ + ".tmp"));
    }
}
