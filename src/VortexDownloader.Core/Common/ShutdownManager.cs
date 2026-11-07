using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VortexDownloader.Core.Common;

/// <summary>
/// Provides system-level actions such as computer shutdown, sleep, and aborting shutdown.
/// Used for auto-shutdown when all downloads complete.
/// </summary>
public static class ShutdownManager
{
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    /// <summary>
    /// Shuts down the computer with an optional safety countdown.
    /// </summary>
    /// <param name="force">If true, forces running applications to close.</param>
    /// <param name="timeoutSeconds">Grace period in seconds before shutdown (default 60s).</param>
    public static void ShutdownComputer(bool force = false, int timeoutSeconds = 60)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string msg = $"VortexDownloader indirmeleri tamamladi. Bilgisayar {timeoutSeconds} saniye icinde kapatilacak.";
                string arguments = force 
                    ? $"/s /f /t {timeoutSeconds} /c \"{msg}\"" 
                    : $"/s /t {timeoutSeconds} /c \"{msg}\"";
                var startInfo = new ProcessStartInfo("shutdown.exe", arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(startInfo);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"ShutdownComputer failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Puts the computer into sleep / standby state.
    /// </summary>
    public static void SleepComputer()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetSuspendState(false, false, false);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"SleepComputer failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Aborts a pending system shutdown.
    /// </summary>
    public static void AbortShutdown()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var startInfo = new ProcessStartInfo("shutdown.exe", "/a")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(startInfo);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"AbortShutdown failed: {ex.Message}");
        }
    }
}
