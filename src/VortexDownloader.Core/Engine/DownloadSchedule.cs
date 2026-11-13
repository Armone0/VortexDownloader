using VortexDownloader.Core.Models;

namespace VortexDownloader.Core.Engine;

/// <summary>Scheduled start ("Başlangıç: Saat seç"). StartAt is stored in UTC.</summary>
public static class DownloadSchedule
{
    /// <summary>Queued with a start time still in the future: the queue must skip it.</summary>
    public static bool IsWaiting(DownloadTask task, DateTime utcNow) =>
        task.Status == DownloadStatus.Queued && task.StartAt is { } at && at.ToUniversalTime() > utcNow;

    /// <summary>Queued with a start time that has arrived (or passed, e.g. the app was closed at that time).</summary>
    public static bool IsDue(DownloadTask task, DateTime utcNow) =>
        task.Status == DownloadStatus.Queued && task.StartAt is { } at && at.ToUniversalTime() <= utcNow;

    /// <summary>
    /// Local time for an hour:minute pick: today, or tomorrow when that minute is already over
    /// (02:00 picked at 23:00 means tomorrow). The current minute stays today, so it starts at once.
    /// </summary>
    public static DateTime NextOccurrence(DateTime localNow, int hour, int minute)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hour);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hour, 23);
        ArgumentOutOfRangeException.ThrowIfNegative(minute);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minute, 59);

        var candidate = localNow.Date.AddHours(hour).AddMinutes(minute);
        var currentMinute = localNow.Date.AddHours(localNow.Hour).AddMinutes(localNow.Minute);
        return candidate < currentMinute ? candidate.AddDays(1) : candidate;
    }

    /// <summary>Card text, e.g. "⏰ 02:00'de başlayacak" or "⏰ yarın 03:00'te başlayacak".</summary>
    public static string FormatLabel(DateTime startAtUtc, DateTime localNow)
    {
        var local = startAtUtc.ToLocalTime();
        string day = (local.Date - localNow.Date).Days switch
        {
            <= 0 => "",
            1 => "yarın ",
            _ => $"{local:dd.MM} "
        };
        return $"⏰ {day}{local:HH:mm}'{TimeSuffix(local.Hour, local.Minute)} başlayacak";
    }

    /// <summary>Turkish locative suffix for a spoken clock time ("02:00'de", "03:00'te", "02:30'da").</summary>
    public static string TimeSuffix(int hour, int minute)
    {
        int n = minute != 0 ? minute : hour;
        if (n == 0) return "da"; // sıfır
        int ones = n % 10;
        if (ones != 0)
        {
            return ones switch { 3 or 4 or 5 => "te", 6 or 9 => "da", _ => "de" }; // üç/dört/beş, altı/dokuz, bir/iki/yedi/sekiz
        }
        return (n / 10) switch { 1 or 3 => "da", 4 => "ta", _ => "de" }; // on/otuz, kırk, yirmi/elli
    }
}
