using System.IO;
using System.Text.RegularExpressions;
using VortexDownloader.Core.Models;

namespace VortexDownloader.Core.Engine;

/// <summary>Finds an unfinished download that a fresh browser capture can continue (same file, same size).</summary>
public static partial class ExpiredLinkMatcher
{
    public static DownloadTask? FindMatch(IEnumerable<DownloadTask> tasks, string? fileName, long? fileSize)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        string key = NormalizeName(fileName);

        return tasks
            .Where(t => (t.LinkExpired || t.Status is DownloadStatus.Failed or DownloadStatus.Paused)
                        && t.Status != DownloadStatus.Completed
                        && t.TotalBytes > 0
                        && (fileSize is not > 0 || fileSize == t.TotalBytes)
                        && string.Equals(NormalizeName(t.FileName), key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.LinkExpired)
            .ThenByDescending(t => t.CreatedAt)
            .FirstOrDefault();
    }

    // "video (1).mp4" and "video.mp4" are the same file: unique-path suffixes are added locally.
    private static string NormalizeName(string name) =>
        DuplicateSuffix().Replace(Path.GetFileNameWithoutExtension(name), "") + Path.GetExtension(name);

    [GeneratedRegex(@"\s*\(\d+\)$")]
    private static partial Regex DuplicateSuffix();
}
