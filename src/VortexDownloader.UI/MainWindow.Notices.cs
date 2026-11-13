using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Sources;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI;

/// <summary>Update notice in the sidebar and opt-in clipboard link watching.</summary>
public partial class MainWindow
{
    // ==================== Update notice ====================
    private readonly CancellationTokenSource _updateCts = new();
    private string? _bannerVersion;

    private async Task CheckForUpdatesOnStartupAsync()
    {
        var settings = AppSettings.Current;
        if (!settings.CheckUpdatesOnStartup) return;
        ShowUpdateBanner(settings.LatestKnownVersion, respectSkip: true); // last result, if still newer
        if (settings.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), _updateCts.Token);
            ShowUpdateBanner(await UpdateChecker.FindNewerVersionAsync(_updateCts.Token), respectSkip: true);
        }
        catch
        {
            // Offline, rate limited or closing: the automatic check stays silent.
        }
    }

    /// <summary>Shows the sidebar notice when <paramref name="version"/> is newer than this build.</summary>
    internal void ShowUpdateBanner(string? version, bool respectSkip)
    {
        if (!UpdateVersion.IsNewer(version, AppInfo.Version)) return;
        if (respectSkip && version == AppSettings.Current.SkippedUpdateVersion) return;
        _bannerVersion = version;
        TxtUpdateBanner.Text = string.Format(LocalizationManager.GetString("Str_UpdateAvailable", "v{0} çıktı"), version);
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void BtnUpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_bannerVersion != null) UpdateChecker.OpenReleasePage(_bannerVersion);
    }

    private void BtnUpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.SkippedUpdateVersion = _bannerVersion;
        AppSettings.Current.Save();
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void BtnUpdateClose_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    // ==================== Clipboard watching ====================
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private HwndSource? _clipboardSource;
    private string? _lastClipboardUrl;

    /// <summary>Starts or stops the listener to match the setting; safe to call repeatedly.</summary>
    internal void ApplyClipboardWatch(bool enabled)
    {
        if (enabled == (_clipboardSource != null)) return;
        if (enabled)
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source == null || !AddClipboardFormatListener(source.Handle)) return;
            source.AddHook(ClipboardHook);
            _clipboardSource = source;
        }
        else
        {
            RemoveClipboardFormatListener(_clipboardSource!.Handle);
            _clipboardSource.RemoveHook(ClipboardHook);
            _clipboardSource = null;
            _lastClipboardUrl = null;
        }
    }

    private IntPtr ClipboardHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Read later: the copying app may still hold the clipboard while it notifies us.
        if (msg == WM_CLIPBOARDUPDATE) Dispatcher.BeginInvoke(DispatcherPriority.Background, OnClipboardChanged);
        return IntPtr.Zero;
    }

    private void OnClipboardChanged()
    {
        if (_clipboardSource == null || FullscreenDetector.IsFullscreenAppRunning()) return;
        // Ignore what Vortex itself copied (its text boxes, SHA-256 chip).
        GetWindowThreadProcessId(GetClipboardOwner(), out uint ownerPid);
        if (ownerPid == (uint)Environment.ProcessId) return;

        string text;
        try
        {
            if (!Clipboard.ContainsText()) return;
            text = Clipboard.GetText();
        }
        catch (ExternalException)
        {
            return; // clipboard busy
        }

        // Clipboard contents are never logged.
        if (!ClipboardLinkFilter.TryGetDownloadUrl(text, out string url) || url == _lastClipboardUrl) return;
        _lastClipboardUrl = url;
        EnqueueBrowserPrompt(new BrowserPromptItem(url, null, null, null, null, null, RequireConfirmation: true));
    }
}
