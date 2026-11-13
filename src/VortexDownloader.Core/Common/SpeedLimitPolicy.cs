namespace VortexDownloader.Core.Common;

/// <summary>Pure rules for game / fullscreen mode throttling.</summary>
public static class SpeedLimitPolicy
{
    /// <summary>
    /// Maps a SHQueryUserNotificationState value to "a fullscreen app is running":
    /// QUNS_BUSY (2), QUNS_RUNNING_D3D_FULL_SCREEN (3), QUNS_PRESENTATION_MODE (4).
    /// </summary>
    public static bool IsFullscreenState(int state) => state is 2 or 3 or 4;

    /// <summary>Stricter of two limits in bytes/sec; 0 (or less) means unlimited.</summary>
    public static long CombineLimits(long user, long game)
    {
        if (user <= 0) return Math.Max(game, 0);
        if (game <= 0) return user;
        return Math.Min(user, game);
    }
}
