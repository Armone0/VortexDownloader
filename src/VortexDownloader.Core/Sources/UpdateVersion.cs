namespace VortexDownloader.Core.Sources;

/// <summary>Version parsing for the in-app update check (tags look like "v1.9.2", the app's informational version like "1.9.1+a79b16a").</summary>
public static class UpdateVersion
{
    /// <summary>Parses "v1.2.3", "1.2.3+hash" or "1.2" (missing parts count as 0); null for anything else.</summary>
    public static Version? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        if (s.Length == 0 || !s.All(c => char.IsAsciiDigit(c) || c == '.')) return null;
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>True only when both parse and <paramref name="latest"/> is strictly newer.</summary>
    public static bool IsNewer(string? latest, string? current) =>
        Parse(latest) is { } l && Parse(current) is { } c && l > c;

    /// <summary>Highest non-prerelease release newer than <paramref name="current"/>, or null.</summary>
    public static (Version Version, GitHubRelease Release)? FindNewer(IEnumerable<GitHubRelease> releases, string? current)
    {
        if (Parse(current) is not { } cur) return null;
        (Version Version, GitHubRelease Release)? best = null;
        foreach (var r in releases)
        {
            if (r.Prerelease || Parse(r.Tag) is not { } v || v <= cur) continue;
            if (best is not { } b || v > b.Version) best = (v, r);
        }
        return best;
    }
}
