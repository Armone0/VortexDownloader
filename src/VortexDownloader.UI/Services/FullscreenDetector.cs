using System.Runtime.InteropServices;
using VortexDownloader.Core.Common;

namespace VortexDownloader.UI.Services;

public static class FullscreenDetector
{
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    public static bool IsFullscreenState(int state) => SpeedLimitPolicy.IsFullscreenState(state);

    /// <summary>True while a fullscreen app (game, video, presentation) is in the foreground.</summary>
    public static bool IsFullscreenAppRunning()
    {
        try
        {
            return SHQueryUserNotificationState(out int state) == 0 && IsFullscreenState(state);
        }
        catch
        {
            return false; // detection is best-effort; never throttle on failure
        }
    }
}
