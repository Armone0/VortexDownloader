using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace VortexDownloader.UI;

/// <summary>
/// Native Windows 11 Shell_NotifyIcon implementation for System Tray.
/// Eliminates the need for Windows Forms dependencies and supports animated vortex spinner.
/// </summary>
public class VortexTrayIcon : IDisposable
{
    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 101;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIIF_INFO = 0x00000001;

    private const int ANIM_FRAME_COUNT = 12;

    private readonly Window _window;
    private readonly Action _onRestore;
    private readonly Action _onExit;
    private readonly Action _onStartAll;
    private readonly Action _onPauseAll;

    private readonly List<ImageSource> _taskbarFrames = new(ANIM_FRAME_COUNT);
    private IntPtr _defaultIcon = IntPtr.Zero;
    private int _currentAnimFrame = 0;
    private bool _isAnimating = false;
    private readonly DispatcherTimer _animTimer;

    private NOTIFYICONDATA _nid;
    private bool _isCreated;
    private HwndSource? _hwndSource;

    public event Action<int>? FrameChanged;
    public IReadOnlyList<ImageSource> TaskbarFrames => _taskbarFrames;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    public VortexTrayIcon(Window window, Action onRestore, Action onStartAll, Action onPauseAll, Action onExit)
    {
        _window = window;
        _onRestore = onRestore;
        _onStartAll = onStartAll;
        _onPauseAll = onPauseAll;
        _onExit = onExit;

        _animTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _animTimer.Tick += OnAnimTimerTick;

        _window.SourceInitialized += (_, _) => InitializeTray();
    }

    private void InitializeTray()
    {
        IntPtr hwnd = new WindowInteropHelper(_window).Handle;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);

        try
        {
            var streamUri = new Uri("pack://application:,,,/Assets/vortex_tray.ico", UriKind.Absolute);
            var sri = Application.GetResourceStream(streamUri);
            if (sri?.Stream != null)
            {
                using var icon = new System.Drawing.Icon(sri.Stream, 16, 16);
                _defaultIcon = CopyIcon(icon.Handle);
            }
        }
        catch
        {
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "vortex_tray.ico");
                if (File.Exists(iconPath))
                {
                    _defaultIcon = LoadImage(IntPtr.Zero, iconPath, 1 /*IMAGE_ICON*/, 16, 16, 0x00000010 /*LR_LOADFROMFILE*/);
                }
            }
            catch
            {
                // Non-fatal: Tray will use default icon if loading fails
            }
        }

        GenerateRotatingFrames();

        _nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = hwnd,
            uID = 1001,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _defaultIcon,
            szTip = "VortexDownloader - Yüksek Hızlı İndirme Yöneticisi"
        };

        _isCreated = Shell_NotifyIcon(NIM_ADD, ref _nid);
    }

    private void GenerateRotatingFrames()
    {
        System.Drawing.Bitmap? baseBitmap = null;
        try
        {
            var logoUri = new Uri("pack://application:,,,/Assets/vortex_logo.png", UriKind.Absolute);
            var sri = Application.GetResourceStream(logoUri);
            if (sri?.Stream != null)
            {
                baseBitmap = new System.Drawing.Bitmap(sri.Stream);
            }
        }
        catch { }

        if (baseBitmap == null)
        {
            try
            {
                string pngPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "vortex_logo.png");
                if (File.Exists(pngPath))
                {
                    baseBitmap = new System.Drawing.Bitmap(pngPath);
                }
            }
            catch { }
        }

        if (baseBitmap == null && _defaultIcon != IntPtr.Zero)
        {
            try
            {
                using var defaultIconObj = System.Drawing.Icon.FromHandle(_defaultIcon);
                baseBitmap = defaultIconObj.ToBitmap();
            }
            catch { }
        }

        if (baseBitmap == null) return;

        try
        {
            float step = 360f / ANIM_FRAME_COUNT;
            for (int i = 0; i < ANIM_FRAME_COUNT; i++)
            {
                float angle = i * step;

                // Taskbar Icon (32x32)
                using (var taskbarBmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(taskbarBmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(System.Drawing.Color.Transparent);

                        g.TranslateTransform(16f, 16f);
                        g.RotateTransform(angle);
                        g.TranslateTransform(-16f, -16f);
                        g.DrawImage(baseBitmap, 0, 0, 32, 32);
                    }

                    IntPtr hTaskbarIcon = taskbarBmp.GetHicon();
                    try
                    {
                        var bs = Imaging.CreateBitmapSourceFromHIcon(
                            hTaskbarIcon,
                            Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        bs.Freeze();
                        _taskbarFrames.Add(bs);
                    }
                    finally
                    {
                        DestroyIcon(hTaskbarIcon);
                    }
                }
            }
        }
        catch
        {
            // Graceful fallback to static icon
        }
        finally
        {
            baseBitmap.Dispose();
        }
    }

    private void OnAnimTimerTick(object? sender, EventArgs e)
    {
        if (!_isCreated || !_isAnimating) return;

        _currentAnimFrame = (_currentAnimFrame + 1) % ANIM_FRAME_COUNT;
        FrameChanged?.Invoke(_currentAnimFrame);
    }

    public void SetIsDownloading(bool isDownloading, string? speedText = null)
    {
        if (_isAnimating == isDownloading)
        {
            if (isDownloading && !string.IsNullOrEmpty(speedText) && _isCreated)
            {
                var data = _nid;
                data.uFlags = NIF_TIP;
                data.szTip = $"VortexDownloader - İndiriliyor ({speedText})";
                Shell_NotifyIcon(NIM_MODIFY, ref data);
            }
            return;
        }

        _isAnimating = isDownloading;
        if (isDownloading)
        {
            _currentAnimFrame = 0;
            _animTimer.Start();
            if (_isCreated)
            {
                var data = _nid;
                data.uFlags = NIF_TIP;
                data.szTip = !string.IsNullOrEmpty(speedText)
                    ? $"VortexDownloader - İndiriliyor ({speedText})"
                    : "VortexDownloader - İndiriliyor...";
                Shell_NotifyIcon(NIM_MODIFY, ref data);
            }
            FrameChanged?.Invoke(0);
        }
        else
        {
            _animTimer.Stop();
            if (_isCreated)
            {
                var data = _nid;
                data.uFlags = NIF_TIP;
                data.szTip = "VortexDownloader - Yüksek Hızlı İndirme Yöneticisi";
                Shell_NotifyIcon(NIM_MODIFY, ref data);
            }
            FrameChanged?.Invoke(-1);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int eventId = lParam.ToInt32();
            if (eventId == WM_LBUTTONDBLCLK)
            {
                _onRestore();
                handled = true;
            }
            else if (eventId == WM_RBUTTONUP)
            {
                ShowContextMenu(hwnd);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu(IntPtr hwnd)
    {
        IntPtr hMenu = CreatePopupMenu();
        try
        {
            AppendMenu(hMenu, 0, 1, "VortexDownloader'ı Aç");
            AppendMenu(hMenu, 0x00000800 /*MF_SEPARATOR*/, 0, string.Empty);
            AppendMenu(hMenu, 0, 2, "Tümünü Başlat");
            AppendMenu(hMenu, 0, 3, "Tümünü Duraklat");
            AppendMenu(hMenu, 0x00000800 /*MF_SEPARATOR*/, 0, string.Empty);
            AppendMenu(hMenu, 0, 4, "Çıkış");

            GetCursorPos(out var pt);
            SetForegroundWindow(hwnd);
            uint cmd = TrackPopupMenu(hMenu, 0x0100 /*TPM_RETURNCMD*/ | 0x0002 /*TPM_RIGHTBUTTON*/, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
            PostMessage(hwnd, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);

            switch (cmd)
            {
                case 1: _onRestore(); break;
                case 2: _onStartAll(); break;
                case 3: _onPauseAll(); break;
                case 4: _onExit(); break;
            }
        }
        finally
        {
            if (hMenu != IntPtr.Zero)
            {
                DestroyMenu(hMenu);
            }
        }
    }

    public void ShowNotification(string title, string message)
    {
        if (!_isCreated) return;

        try
        {
            var data = _nid;
            data.uFlags |= NIF_INFO;
            data.szInfoTitle = title != null && title.Length > 63 ? title.Substring(0, 63) : (title ?? "VortexDownloader");
            data.szInfo = message != null && message.Length > 255 ? message.Substring(0, 255) : (message ?? "");
            data.dwInfoFlags = NIIF_INFO;

            Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch
        {
            // Suppress tray notification failures on restricted environments
        }
    }

    public void Dispose()
    {
        _animTimer.Stop();

        if (_hwndSource != null)
        {
            try
            {
                _hwndSource.RemoveHook(WndProc);
            }
            catch
            {
            }
            _hwndSource = null;
        }

        if (_isCreated)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            _isCreated = false;
        }

        if (_defaultIcon != IntPtr.Zero)
        {
            DestroyIcon(_defaultIcon);
            _defaultIcon = IntPtr.Zero;
        }
    }
}
