using System.Globalization;
using VortexDownloader.Core.Models;

namespace VortexDownloader.Core.Common;

public record MonthlyDownloadStats(long TotalBytes, int Count, double AverageBytesPerSec);

/// <summary>"Bu ay" summary line computed from the completed tasks the engine already keeps.</summary>
public static class DownloadStats
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static MonthlyDownloadStats ForMonth(IEnumerable<DownloadTask> tasks, DateTime localNow)
    {
        var done = tasks.Where(t => t.Status == DownloadStatus.Completed && t.CompletedAt is { } c
            && c.ToLocalTime().Year == localNow.Year && c.ToLocalTime().Month == localNow.Month).ToList();

        static long Size(DownloadTask t) => Math.Max(0, t.TotalBytes > 0 ? t.TotalBytes : t.DownloadedBytes);

        // Tasks finished before active time was tracked have ActiveSeconds = 0 and are left out of the average.
        var timed = done.Where(t => t.ActiveSeconds > 0).ToList();
        double seconds = timed.Sum(t => t.ActiveSeconds);
        double avg = seconds > 0 ? timed.Sum(Size) / seconds : 0;

        return new MonthlyDownloadStats(done.Sum(Size), done.Count, avg);
    }

    /// <summary>e.g. "Bu ay: 42,3 GB · 128 indirme · ortalama 18,4 MB/s".</summary>
    public static string Format(MonthlyDownloadStats s)
    {
        const double MB = 1024 * 1024, GB = MB * 1024;
        string size = s.TotalBytes >= GB
            ? (s.TotalBytes / GB).ToString("0.0", Tr) + " GB"
            : (s.TotalBytes / MB).ToString("0.0", Tr) + " MB";
        string text = $"Bu ay: {size} · {s.Count} indirme";
        if (s.AverageBytesPerSec > 0)
        {
            text += $" · ortalama {(s.AverageBytesPerSec / MB).ToString("0.0", Tr)} MB/s";
        }
        return text;
    }
}
