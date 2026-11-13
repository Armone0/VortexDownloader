using System.IO;
using System.Text.Json.Serialization;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Network;

namespace VortexDownloader.Core.Models;

public class DownloadTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Url { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long TotalBytes { get; set; } = -1;
    public long DownloadedBytes { get; set; }
    public double SpeedBytesPerSec { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public bool SupportsRange { get; set; } = true;
    public int MaxChunks { get; set; } = 8;
    public List<DownloadChunk> Chunks { get; set; } = [];
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public string TempDirectory { get; set; } = string.Empty;
    public string? Cookies { get; set; }
    public string? Referrer { get; set; }
    public string? UserAgent { get; set; }
    public string? Sha256 { get; set; }
    public ScanStatus ScanStatus { get; set; }
    /// <summary>Page the download was started from; opened again to get a fresh link when this one expires.</summary>
    public string? SourcePageUrl { get; set; }
    /// <summary>Server rejected the link (401/403/404/410 or size changed); downloaded chunks are kept for a refresh.</summary>
    public bool LinkExpired { get; set; }
    /// <summary>Extra URLs serving the same file; chunks are spread over <see cref="Url"/> and these.</summary>
    public List<string> MirrorUrls { get; set; } = [];
    /// <summary>Sources (primary or mirror) that failed during the current run. Replaced, never mutated, so readers need no lock.</summary>
    [JsonIgnore]
    public IReadOnlyCollection<string> DeadSources { get; set; } = [];
    /// <summary>Scheduled start (UTC). While in the future the queued task is skipped by the queue; cleared when due or paused.</summary>
    public DateTime? StartAt { get; set; }
    /// <summary>SHA-256 the user expects (lowercase hex); compared with <see cref="Sha256"/> after completion. Integrity only, not safety.</summary>
    public string? ExpectedSha256 { get; set; }
    /// <summary>Seconds spent in Downloading state (excludes queue/schedule/pause time); used for average speed stats.</summary>
    public double ActiveSeconds { get; set; }
    /// <summary>HTTP Basic user name, sent only to <see cref="AuthOrigin"/>.</summary>
    public string? AuthUser { get; set; }
    /// <summary>scheme://host[:port] the user gave the credentials for; fixed at creation (a refreshed <see cref="Url"/> does not move it).</summary>
    public string? AuthOrigin { get; set; }
    [JsonIgnore]
    public string? AuthPassword { get; set; }
    /// <summary>tasks.json form of <see cref="AuthPassword"/>: DPAPI (current user), never plain text.</summary>
    public string? AuthPasswordProtected
    {
        get => SecretProtector.Protect(AuthPassword);
        set => AuthPassword = SecretProtector.Unprotect(value);
    }

    [JsonIgnore]
    public HttpCredential? Credential =>
        string.IsNullOrEmpty(AuthUser) || AuthOrigin == null ? null : new HttpCredential(AuthOrigin, AuthUser, AuthPassword ?? "");

    private readonly object _chunksLock = new();

    [JsonIgnore]
    public object ChunksLock => _chunksLock;

    [JsonIgnore]
    public double ProgressPercentage
    {
        get
        {
            if (Status == DownloadStatus.Completed) return 100.0;
            if (TotalBytes <= 0) return 0.0;
            return Math.Clamp((double)DownloadedBytes / TotalBytes * 100.0, 0.0, 100.0);
        }
    }

    [JsonIgnore]
    public string FormattedSpeed => FormatBytesPerSec(SpeedBytesPerSec);

    [JsonIgnore]
    public string FormattedProgress => TotalBytes > 0
        ? $"{FormatBytes(DownloadedBytes)} / {FormatBytes(TotalBytes)} ({ProgressPercentage:F1}%)"
        : $"{FormatBytes(DownloadedBytes)} (Bilinmiyor)";

    [JsonIgnore]
    public int CompletedChunkCount
    {
        get
        {
            lock (_chunksLock)
            {
                return Chunks.Count(c => c.IsCompleted);
            }
        }
    }

    [JsonIgnore]
    public int ActiveChunkCount
    {
        get
        {
            lock (_chunksLock)
            {
                return Chunks.Count(c => c.IsActive && !c.IsCompleted);
            }
        }
    }

    public List<DownloadChunk> CloneChunks()
    {
        lock (_chunksLock)
        {
            return new List<DownloadChunk>(Chunks);
        }
    }

    public void RecalculateDownloadedBytes()
    {
        lock (_chunksLock)
        {
            if (Chunks.Count > 0)
            {
                long sum = 0;
                foreach (var chunk in Chunks)
                {
                    sum += chunk.DownloadedBytes;
                }
                DownloadedBytes = sum;
            }
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < units.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {units[order]}";
    }

    public static string FormatBytesPerSec(double bytesPerSec)
    {
        if (bytesPerSec <= 0) return "0 KB/s";
        string[] units = ["B/s", "KB/s", "MB/s", "GB/s"];
        double len = bytesPerSec;
        int order = 0;
        while (len >= 1024 && order < units.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {units[order]}";
    }
}
