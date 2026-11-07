using System.Runtime.InteropServices;

namespace VortexDownloader.Core.Common;

/// <summary>
/// Prevents the Windows operating system from entering sleep or hibernation while downloads are active.
/// Fixes Motrix's issue of overnight downloads failing when the PC goes to sleep.
/// </summary>
public static class SleepPreventer
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_AWAYMODE_REQUIRED = 0x00000040;

    private static int _activeDownloads;
    private static readonly object _lock = new();

    public static void StartPreventingSleep()
    {
        lock (_lock)
        {
            if (Interlocked.Increment(ref _activeDownloads) == 1)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
                    }
                }
                catch
                {
                    // Non-critical, ignore on systems without API access
                }
            }
        }
    }

    public static void StopPreventingSleep()
    {
        lock (_lock)
        {
            if (Interlocked.Decrement(ref _activeDownloads) <= 0)
            {
                _activeDownloads = 0;
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        SetThreadExecutionState(ES_CONTINUOUS);
                    }
                }
                catch
                {
                    // Non-critical
                }
            }
        }
    }
}
