using System.IO;
using System.Security.Cryptography;
using VortexDownloader.Core.Media;
using VortexDownloader.Core.Models;

namespace VortexDownloader.Core.Common;

public static class FileHasher
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        // Share ReadWrite|Delete so hashing never blocks the user from moving or deleting the file.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    public static bool Matches(string? actual, string? expected) =>
        !string.IsNullOrWhiteSpace(actual) && !string.IsNullOrWhiteSpace(expected) &&
        string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Validates a user-typed SHA-256: whitespace is ignored, case-insensitive, must be 64 hex chars.
    /// Empty input is valid (no expectation) and yields null.
    /// </summary>
    public static bool TryNormalizeSha256(string? input, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(input)) return true;
        string hex = string.Concat(input.Where(c => !char.IsWhiteSpace(c)));
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) return false;
        normalized = hex.ToLowerInvariant();
        return true;
    }
}

/// <summary>
/// Scans one file with Microsoft Defender's MpCmdRun. Per Microsoft docs, -Scan returns 0 when no threat is found
/// and 2 when a threat is found (and, with -DisableRemediation, left untouched). Anything else is treated as "could not scan".
/// </summary>
public class DefenderScanner
{
    private readonly IProcessRunner _runner;
    private readonly string _exePath;
    private readonly TimeSpan _timeout;

    public static string DefaultExePath => Path.Combine(
        Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Windows Defender", "MpCmdRun.exe");

    public DefenderScanner(IProcessRunner? runner = null, string? exePath = null, TimeSpan? timeout = null)
    {
        _runner = runner ?? new DefaultProcessRunner();
        _exePath = exePath ?? DefaultExePath;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task<ScanStatus> ScanAsync(string filePath, CancellationToken ct = default)
    {
        // Missing MpCmdRun usually means a third-party antivirus replaced Defender.
        if (!File.Exists(_exePath) || !File.Exists(filePath)) return ScanStatus.Unavailable;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            // DefaultProcessRunner kills the process tree when the token fires (timeout or app close).
            var result = await _runner.RunAsync(
                _exePath,
                ["-Scan", "-ScanType", "3", "-File", filePath, "-DisableRemediation"],
                ct: timeoutCts.Token).ConfigureAwait(false);

            return result.ExitCode switch
            {
                0 => ScanStatus.Clean,
                2 => ScanStatus.Threat,
                _ => ScanStatus.Unavailable
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ScanStatus.Unavailable; // timed out
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ScanStatus.Unavailable; // e.g. access denied launching MpCmdRun
        }
    }
}
