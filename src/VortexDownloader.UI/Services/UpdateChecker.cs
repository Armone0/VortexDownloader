using System.Diagnostics;
using VortexDownloader.Core.Sources;

namespace VortexDownloader.UI.Services;

/// <summary>Asks GitHub for the newest release number. Only notifies and links; never downloads or runs anything.</summary>
public static class UpdateChecker
{
    private const string Owner = "Armone0";
    private const string Repo = "VortexDownloader";

    /// <summary>Newer non-prerelease version ("1.9.2"), or null when up to date. Throws on network/GitHub errors.</summary>
    public static async Task<string?> FindNewerVersionAsync(CancellationToken ct = default)
    {
        var releases = await new GitHubReleaseClient().GetReleasesAsync(Owner, Repo, ct);
        var newer = UpdateVersion.FindNewer(releases, AppInfo.Version);
        var settings = AppSettings.Current;
        settings.LastUpdateCheckUtc = DateTime.UtcNow;
        if (newer is { } n) settings.LatestKnownVersion = n.Version.ToString(3);
        settings.Save();
        return newer?.Version.ToString(3);
    }

    /// <summary>Built from our own constants and a parsed version, never from text in the response.</summary>
    public static void OpenReleasePage(string version)
    {
        if (UpdateVersion.Parse(version) is not { } v) return;
        try
        {
            Process.Start(new ProcessStartInfo($"https://github.com/{Owner}/{Repo}/releases/tag/v{v.ToString(3)}") { UseShellExecute = true });
        }
        catch { /* no default browser */ }
    }
}
