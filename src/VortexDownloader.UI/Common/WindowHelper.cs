using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VortexDownloader.UI.Common;

public static class WindowHelper
{
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public static void BringToFrontAndFocus(Window window)
    {
        if (window == null) return;

        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.Invoke(() => BringToFrontAndFocus(window));
            return;
        }

        try
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            var helper = new WindowInteropHelper(window);
            IntPtr hWnd = helper.Handle;

            if (hWnd == IntPtr.Zero)
            {
                helper.EnsureHandle();
                hWnd = helper.Handle;
            }

            if (hWnd != IntPtr.Zero)
            {
                IntPtr foregroundHwnd = GetForegroundWindow();
                uint foregroundThreadId = foregroundHwnd != IntPtr.Zero
                    ? GetWindowThreadProcessId(foregroundHwnd, IntPtr.Zero)
                    : 0;
                uint currentThreadId = GetCurrentThreadId();

                bool attached = false;
                if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                {
                    attached = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                }

                try
                {
                    ShowWindow(hWnd, SW_SHOW);
                    BringWindowToTop(hWnd);
                    SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    if (!window.Topmost)
                    {
                        SetWindowPos(hWnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                    }
                    SetForegroundWindow(hWnd);
                }
                finally
                {
                    if (attached)
                    {
                        AttachThreadInput(currentThreadId, foregroundThreadId, false);
                    }
                }
            }

            window.Activate();
            window.Focus();
        }
        catch
        {
            // Suppress interop exceptions during focus transition
        }
    }
}
