using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VortexDownloader.UI.Common;

public static class WindowHelper
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    public static void BringToFront(Window window)
    {
        if (window == null) return;

        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.Invoke(() => BringToFront(window));
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

            if (hWnd != IntPtr.Zero)
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }

            window.Activate();
            window.Focus();
        }
        catch
        {
            // Suppress interop exceptions during focus transition
        }
    }

    public static void BringToFrontAndFocus(Window window) => BringToFront(window);
}
