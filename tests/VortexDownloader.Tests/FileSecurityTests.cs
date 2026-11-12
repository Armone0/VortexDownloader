using System.IO;
using System.Text;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Media;
using VortexDownloader.Core.Models;
using Xunit;

namespace VortexDownloader.Tests;

public class FileSecurityTests
{
    private class FakeRunner : IProcessRunner
    {
        public int ExitCode { get; set; }
        public bool Hang { get; set; }
        public List<string>? LastArgs { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(string fileName, string arguments,
            Action<string>? onOutputLine = null, Action<string>? onErrorLine = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Scanner must use the argument-list overload.");

        public async Task<ProcessExecutionResult> RunAsync(string fileName, IEnumerable<string> argumentList,
            Action<string>? onOutputLine = null, Action<string>? onErrorLine = null, CancellationToken ct = default)
        {
            LastArgs = argumentList.ToList();
            if (Hang) await Task.Delay(Timeout.Infinite, ct);
            return new ProcessExecutionResult(ExitCode, string.Empty, string.Empty);
        }
    }

    private static string TempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "vortex_sec_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task ComputeSha256_KnownContent_MatchesKnownHash()
    {
        string path = TempFile("abc");
        try
        {
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                await FileHasher.ComputeSha256Async(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("  BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD \r\n", true)]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", true)]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ae", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Matches_IsCaseInsensitiveAndTrimmed(string? expected, bool match)
    {
        Assert.Equal(match, FileHasher.Matches("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", expected));
    }

    [Theory]
    [InlineData(0, ScanStatus.Clean)]
    [InlineData(2, ScanStatus.Threat)]
    [InlineData(1, ScanStatus.Unavailable)]
    [InlineData(-2147024894, ScanStatus.Unavailable)]
    public async Task Scan_MapsExitCodes(int exitCode, ScanStatus expected)
    {
        string exe = TempFile("fake exe");
        string target = TempFile("payload with spaces");
        try
        {
            var runner = new FakeRunner { ExitCode = exitCode };
            var status = await new DefenderScanner(runner, exe).ScanAsync(target);
            Assert.Equal(expected, status);
            Assert.Equal(["-Scan", "-ScanType", "3", "-File", target, "-DisableRemediation"], runner.LastArgs);
        }
        finally { File.Delete(exe); File.Delete(target); }
    }

    [Fact]
    public async Task Scan_MissingExecutable_IsUnavailableWithoutRunning()
    {
        string target = TempFile("x");
        try
        {
            var runner = new FakeRunner();
            var status = await new DefenderScanner(runner, Path.Combine(Path.GetTempPath(), "no_such_MpCmdRun.exe")).ScanAsync(target);
            Assert.Equal(ScanStatus.Unavailable, status);
            Assert.Null(runner.LastArgs);
        }
        finally { File.Delete(target); }
    }

    [Fact]
    public async Task Scan_Timeout_IsUnavailable_ButCallerCancelThrows()
    {
        string exe = TempFile("fake exe");
        string target = TempFile("x");
        try
        {
            var timedOut = new DefenderScanner(new FakeRunner { Hang = true }, exe, TimeSpan.FromMilliseconds(50));
            Assert.Equal(ScanStatus.Unavailable, await timedOut.ScanAsync(target));

            using var cts = new CancellationTokenSource(50);
            var slow = new DefenderScanner(new FakeRunner { Hang = true }, exe, TimeSpan.FromMinutes(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow.ScanAsync(target, cts.Token));
        }
        finally { File.Delete(exe); File.Delete(target); }
    }
}
