using System.Reflection;

namespace VortexDownloader.UI.Services;

/// <summary>Single source of truth for the version shown in the UI (read from the assembly, so csproj is the only place to bump).</summary>
public static class AppInfo
{
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?");

    public static string VersionText => $"v{Version}";
    public static string SettingsBrandText => $"VORTEX v{Version}";
    public static string EditionText => $"v{Version} • Professional Edition";
}
