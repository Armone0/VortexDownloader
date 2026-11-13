using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Network;
using VortexDownloader.Core.Media;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Plugins;
using VortexDownloader.UI.Common;
using VortexDownloader.UI.Services;
using VortexDownloader.UI.ViewModels;
using VortexDownloader.UI.Views;

namespace VortexDownloader.UI;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly MainViewModel _viewModel;
    private readonly PluginStore _pluginStore = new(PluginStore.DefaultRoot);
    private PluginCatalog _pluginCatalog = new();
    private VortexTrayIcon? _trayIcon;
    private HttpListener? _extensionBridgeListener;
    private CancellationTokenSource? _bridgeCts;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private bool _isRealExit;
    private System.Windows.Media.ImageSource? _defaultWindowIcon;

    private record BrowserPromptItem(
        string Url,
        string? FileName,
        long? Size,
        string? Cookies,
        string? Referrer,
        string? UserAgent,
        string? PageUrl = null);

    private readonly Queue<BrowserPromptItem> _browserPromptQueue = new();
    private bool _isPromptShowing = false;
    private string? _activePromptUrl = null;
    private readonly ConcurrentDictionary<string, DateTime> _fallbackBrowserUrls = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _pairLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _pairDeniedUntil = new(StringComparer.OrdinalIgnoreCase);

    private static string EnsureBridgeToken()
    {
        var settings = AppSettings.Current;
        if (string.IsNullOrEmpty(settings.BridgeToken))
        {
            settings.BridgeToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            settings.Save();
        }
        return settings.BridgeToken;
    }

    // A request is trusted only from an origin the user paired AND carrying this install's secret
    private static bool IsPairedRequest(string? origin, string? token)
    {
        if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(token)) return false;
        if (!AppSettings.Current.PairedExtensionOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(EnsureBridgeToken()));
    }

    private async Task HandlePairRequestAsync(HttpListenerContext context, string origin)
    {
        var settings = AppSettings.Current;
        bool approved = settings.PairedExtensionOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
        if (!approved)
        {
            if (_pairDeniedUntil.TryGetValue(origin, out var until) && DateTime.UtcNow < until)
            {
                BridgeLog($"<-- 403 Pair denied recently for '{origin}'");
                await WriteJsonAsync(context, 403, "{\"status\":\"pair_denied\"}");
                return;
            }

            await _pairLock.WaitAsync();
            try
            {
                approved = settings.PairedExtensionOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);
                if (!approved)
                {
                    approved = await Dispatcher.InvokeAsync(() => ConfirmDialog.Ask(
                        IsVisible ? this : null,
                        "Tarayıcı eklentisi bağlanmak istiyor",
                        "Bu eklentiye izin verirsen tarayıcıdaki indirmeler Vortex'e aktarılır. Yalnızca kendi kurduğun VortexDownloader eklentisine izin ver.",
                        $"Eklenti kimliği: {origin[(origin.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/')}",
                        "İzin ver", ""));
                    if (approved)
                    {
                        settings.PairedExtensionOrigins.Add(origin);
                        settings.Save();
                    }
                    else
                    {
                        _pairDeniedUntil[origin] = DateTime.UtcNow.AddMinutes(2);
                    }
                }
            }
            finally { _pairLock.Release(); }
        }

        if (!approved)
        {
            BridgeLog($"<-- 403 Pair rejected by user for '{origin}'");
            await WriteJsonAsync(context, 403, "{\"status\":\"pair_denied\"}");
            return;
        }
        BridgeLog($"<-- 200 Paired extension '{origin}'");
        await WriteJsonAsync(context, 200, JsonSerializer.Serialize(new { status = "paired", token = EnsureBridgeToken() }));
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await using var writer = new StreamWriter(context.Response.OutputStream);
        await writer.WriteAsync(json);
    }

    public MainWindow()
    {
        Log("MainWindow constructor start");
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        MediaPluginPanel.Initialize(_viewModel.Engine);
        MediaPluginPanel.DownloadRequested += ExecuteMediaDownload;
        try { _pluginStore.SeedDefaults(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        ReloadPlugins();

        ThemeManager.ThemeChanged += OnThemeChanged;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;

        _trayIcon = new VortexTrayIcon(
            this,
            RestoreFromTray,
            () => _viewModel.StartAllCommand.Execute(null),
            () => _ = _viewModel.Engine.PauseAllAsync(),
            RealExit);

        _trayIcon.FrameChanged += OnTrayFrameChanged;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.Engine.ThreatDetected += OnThreatDetected;

        // Game mode: cheap shell query every 2 s; keeps running while hidden in the tray
        _gameModeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameModeTimer.Tick += OnGameModeTick;
        _gameModeTimer.Start();

        StartBrowserExtensionBridge();
        Log("MainWindow constructor end");
    }

    private void OnTrayFrameChanged(int frameIndex)
    {
        if (frameIndex >= 0 && _viewModel.HasActiveDownloads && _trayIcon != null && _trayIcon.TaskbarFrames.Count > frameIndex)
        {
            this.Icon = _trayIcon.TaskbarFrames[frameIndex];
        }
        else
        {
            if (_defaultWindowIcon != null)
            {
                this.Icon = _defaultWindowIcon;
            }
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HasActiveDownloads) ||
            e.PropertyName == nameof(MainViewModel.TotalSpeedFormatted) ||
            e.PropertyName == nameof(MainViewModel.TotalActiveProgressRatio) ||
            e.PropertyName == nameof(MainViewModel.HasIndeterminateProgress) ||
            e.PropertyName == nameof(MainViewModel.PausedDownloadsCount))
        {
            UpdateDownloadVisualIndicators();
        }
    }

    private void UpdateDownloadVisualIndicators()
    {
        bool isActive = _viewModel.HasActiveDownloads;
        _trayIcon?.SetIsDownloading(isActive, _viewModel.TotalSpeedFormatted);

        if (AppTaskbarInfo != null)
        {
            if (isActive)
            {
                if (_viewModel.HasIndeterminateProgress)
                {
                    AppTaskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;
                }
                else
                {
                    AppTaskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
                    AppTaskbarInfo.ProgressValue = _viewModel.TotalActiveProgressRatio;
                }
            }
            else if (_viewModel.PausedDownloadsCount > 0 && _viewModel.TotalActiveProgressRatio > 0)
            {
                AppTaskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Paused;
                AppTaskbarInfo.ProgressValue = _viewModel.TotalActiveProgressRatio;
            }
            else
            {
                AppTaskbarInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
                AppTaskbarInfo.ProgressValue = 0.0;
                if (_defaultWindowIcon != null)
                {
                    this.Icon = _defaultWindowIcon;
                }
            }
        }
    }

    private static void Log(string msg)
    {
        try
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VortexDownloader");
            Directory.CreateDirectory(appData);
            File.AppendAllText(Path.Combine(appData, "startup.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [MainWindow] {msg}\n");
        }
        catch { }
    }

    private static void BridgeLog(string msg)
    {
        try
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VortexDownloader");
            Directory.CreateDirectory(appData);
            File.AppendAllText(Path.Combine(appData, "bridge.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n");
        }
        catch { }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyDwmDarkMode(this, ThemeManager.CurrentTheme == "Dark");
    }

    private void OnThemeChanged(string themeName)
    {
        bool isDark = themeName.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        ThemeManager.ApplyDwmDarkMode(this, isDark);
    }

    private void OnThreatDetected(DownloadTask task)
    {
        // Raised on a background thread by the engine's post-download scan.
        Dispatcher.BeginInvoke(() => _trayIcon?.ShowNotification(
            "⚠️ VortexDownloader — Tehdit bulundu",
            $"Windows Defender \"{task.FileName}\" dosyasında tehdit buldu. Dosyayı açmayın."));
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // Normal minimize behavior: Keep window visible on Windows Taskbar so user doesn't lose it
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void RealExit()
    {
        _isRealExit = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Close();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Log("MainWindow_Loaded enter");
        _defaultWindowIcon = this.Icon;
        var settings = AppSettings.Current;
        _viewModel.Engine.MaxConcurrentDownloads = settings.MaxConcurrentDownloads;
        _viewModel.Engine.ScanWithDefender = settings.ScanWithDefender;
        _viewModel.ShutdownWhenCompleted = settings.ShutdownWhenCompleted;
        _viewModel.SelectedMaxConcurrent = settings.MaxConcurrentDownloads switch
        {
            1 => "1 Görev",
            2 => "2 Görev",
            3 => "3 Görev",
            5 => "5 Görev",
            0 => "Sınırsız",
            _ => $"{settings.MaxConcurrentDownloads} Görev"
        };
        await _viewModel.InitializeAsync();
        Log("MainWindow_Loaded exit");
    }

    private readonly System.Windows.Threading.DispatcherTimer _gameModeTimer;

    private void OnGameModeTick(object? sender, EventArgs e) =>
        _viewModel.UpdateGameMode(FullscreenDetector.IsFullscreenAppRunning());

    private static bool _trayNotificationShown = false;

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Log($"MainWindow_Closing enter (_isRealExit={_isRealExit}, CloseToTray={AppSettings.Current.CloseToTray})");
        if (!_isRealExit && !AppSettings.Current.CloseToTray)
        {
            _isRealExit = true;
        }

        if (!_isRealExit)
        {
            // Minimize to tray instead of quitting
            e.Cancel = true;
            Hide();
            Log("MainWindow_Closing minimized to tray (cancelled exit)");

            if (!_trayNotificationShown)
            {
                _trayNotificationShown = true;
                _trayIcon?.ShowNotification(
                    "VortexDownloader",
                    "VortexDownloader arka planda çalışmaya ve tarayıcı indirmelerini yakalamaya devam ediyor.");
            }
            return;
        }

        _bridgeCts?.Cancel();
        try { _extensionBridgeListener?.Close(); } catch { }
        _bridgeCts?.Dispose();
        _gameModeTimer.Stop();
        _gameModeTimer.Tick -= OnGameModeTick;
        _viewModel.Engine.ThreatDetected -= OnThreatDetected;
        _trayIcon?.Dispose();
        await _viewModel.Engine.DisposeAsync();
    }

    private void StartBrowserExtensionBridge()
    {
        try
        {
            _extensionBridgeListener = new HttpListener();
            _extensionBridgeListener.Prefixes.Add("http://127.0.0.1:19741/");
            _extensionBridgeListener.Start();

            BridgeLog("HTTP Bridge started successfully on http://127.0.0.1:19741/");

            _bridgeCts = new CancellationTokenSource();
            _ = Task.Run(() => BridgeListenLoopAsync(_bridgeCts.Token));
        }
        catch (Exception ex)
        {
            BridgeLog($"FATAL: HTTP Bridge failed to bind port 19741: {ex.Message}");
            Log($"Extension bridge listener failed to start: {ex.Message}");
        }
    }

    private async Task BridgeListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _extensionBridgeListener?.IsListening == true)
        {
            try
            {
                var context = await _extensionBridgeListener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleBrowserCaptureRequestAsync(context));
            }
            catch
            {
                break;
            }
        }
    }

    private async Task HandleBrowserCaptureRequestAsync(HttpListenerContext context)
    {
        string method = context.Request.HttpMethod;
        string path = context.Request.Url?.AbsolutePath ?? "/";
        string? host = context.Request.Headers["Host"];
        string? origin = context.Request.Headers["Origin"];
        string? authToken = context.Request.Headers["X-Vortex-Auth"];

        BridgeLog($"--> [{method}] {path} | Host: '{host}' | Origin: '{origin}' | AuthToken: '{(string.IsNullOrEmpty(authToken) ? "None" : "Present")}'");

        try
        {
            // Security 1: Host header validation (Defend against DNS rebinding attacks)
            string? hostName = host;
            int colon = hostName?.LastIndexOf(':') ?? -1;
            if (colon >= 0) hostName = hostName!.Substring(0, colon);
            if (!string.Equals(hostName, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                BridgeLog($"<-- 403 Forbidden: Invalid host header '{host}'");
                context.Response.StatusCode = 403;
                return;
            }

            // Security 2: Origin validation & CORS setup (only browser extensions may call with an Origin)
            if (!string.IsNullOrEmpty(origin))
            {
                bool isExtension = origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                                   origin.StartsWith("edge-extension://", StringComparison.OrdinalIgnoreCase) ||
                                   origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase);
                if (!isExtension)
                {
                    BridgeLog($"<-- 403 Forbidden: Non-extension origin '{origin}'");
                    context.Response.StatusCode = 403;
                    return;
                }
                context.Response.Headers.Add("Access-Control-Allow-Origin", origin);
                context.Response.Headers.Add("Vary", "Origin");
            }

            context.Response.Headers.Add("Access-Control-Allow-Methods", "POST, GET, OPTIONS");
            context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Vortex-Auth, Access-Control-Request-Private-Network");
            context.Response.Headers.Add("Access-Control-Allow-Private-Network", "true");

            // Handle CORS Preflight FIRST before any auth checks
            if (context.Request.HttpMethod == "OPTIONS")
            {
                BridgeLog("<-- 200 OK: Handled CORS Preflight OPTIONS");
                context.Response.StatusCode = 200;
                return;
            }

            // Restore / Bring to Front endpoint for single-instance activation
            if (context.Request.Url?.AbsolutePath.EndsWith("/restore", StringComparison.OrdinalIgnoreCase) == true)
            {
                BridgeLog("<-- 200 OK: Single-Instance Restore requested");
                _ = Dispatcher.InvokeAsync(RestoreFromTray);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                using (var restoreWriter = new StreamWriter(context.Response.OutputStream))
                {
                    await restoreWriter.WriteAsync("{\"status\":\"restored\"}");
                }
                return;
            }

            // Ping / Health Check Endpoint for Extension Status (tells the extension whether it is paired)
            if (context.Request.HttpMethod == "GET")
            {
                BridgeLog("<-- 200 OK: Ping Health Check");
                bool paired = IsPairedRequest(origin, authToken);
                await WriteJsonAsync(context, 200,
                    $"{{\"status\":\"online\",\"version\":\"{AppInfo.Version}\",\"paired\":{(paired ? "true" : "false")}}}");
                return;
            }

            // Pairing: an extension asks for this install's token; the user approves it once in a dialog
            if (context.Request.HttpMethod == "POST" && path.EndsWith("/pair", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(origin))
                {
                    BridgeLog("<-- 403 Pair without extension origin");
                    context.Response.StatusCode = 403;
                    return;
                }
                await HandlePairRequestAsync(context, origin);
                return;
            }

            // Security: only a paired extension carrying this install's random token may enqueue downloads
            if (!IsPairedRequest(origin, authToken))
            {
                BridgeLog("<-- 401 Not paired / token mismatch");
                await WriteJsonAsync(context, 401, "{\"status\":\"pair_required\"}");
                return;
            }

            if (context.Request.HttpMethod == "POST")
            {
                const int MaxBodyChars = 1024 * 1024;
                if (context.Request.ContentLength64 > MaxBodyChars)
                {
                    BridgeLog("<-- 413 Payload Too Large (Content-Length)");
                    context.Response.StatusCode = 413;
                    return;
                }
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var buffer = new char[MaxBodyChars + 1];
                int total = 0, read;
                while (total < buffer.Length && (read = await reader.ReadAsync(buffer, total, buffer.Length - total)) > 0)
                    total += read;
                if (total > MaxBodyChars)
                {
                    BridgeLog("<-- 413 Payload Too Large (body)");
                    context.Response.StatusCode = 413;
                    return;
                }
                string body = new string(buffer, 0, total);
                BridgeLog($"--> [POST] Body ({body.Length} chars): {body}");
                BrowserDownloadPayload? data;
                try { data = JsonSerializer.Deserialize<BrowserDownloadPayload>(body, JsonOptions); }
                catch (JsonException)
                {
                    BridgeLog("<-- 400 Bad Request: Invalid JSON");
                    context.Response.StatusCode = 400;
                    return;
                }

                string? effectiveUrl = data?.Url;
                if (!string.IsNullOrWhiteSpace(effectiveUrl))
                {
                    // Security: Enforce HTTP/HTTPS scheme validation to prevent file:// or command injection URIs
                    if (!Uri.TryCreate(effectiveUrl, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        BridgeLog($"<-- 400 Bad Request: Invalid URL scheme '{effectiveUrl}'");
                        context.Response.StatusCode = 400;
                        return;
                    }

                    // Check if this URL is marked for browser fallback
                    if (_fallbackBrowserUrls.TryGetValue(effectiveUrl, out var expireTime) ||
                        _fallbackBrowserUrls.TryGetValue(uri.AbsoluteUri, out expireTime))
                    {
                        if (DateTime.UtcNow <= expireTime)
                        {
                            BridgeLog($"<-- 409 Conflict: URL is in browser fallback list (delegated to browser): '{effectiveUrl}'");
                            context.Response.StatusCode = 409;
                            context.Response.ContentType = "application/json";
                            using (var conflictWriter = new StreamWriter(context.Response.OutputStream))
                            {
                                await conflictWriter.WriteAsync("{\"status\":\"browser_fallback\"}");
                            }
                            return;
                        }
                        else
                        {
                            _fallbackBrowserUrls.TryRemove(effectiveUrl, out _);
                            _fallbackBrowserUrls.TryRemove(uri.AbsoluteUri, out _);
                        }
                    }

                    string safeFileName = SanitizeFileName(data?.FileName);
                    string? referrer = data?.EffectiveReferrer;
                    BridgeLog($"<-- 200 OK: Enqueued download for '{safeFileName}' ({uri.AbsoluteUri}) | Size: {data?.FileSize} bytes");

                    // Send HTTP 200 response IMMEDIATELY so the browser extension can cancel browser download without timeout!
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    using (var writer = new StreamWriter(context.Response.OutputStream))
                    {
                        await writer.WriteAsync("{\"status\":\"ok\"}");
                    }
                    try { context.Response.Close(); } catch { }

                    // Launch the download prompt on the UI thread asynchronously via the sequential queue
                    EnqueueBrowserPrompt(new BrowserPromptItem(
                        uri.AbsoluteUri,
                        safeFileName,
                        data?.FileSize,
                        data?.Cookies,
                        referrer,
                        data?.UserAgent,
                        data?.PageUrl));

                    return;
                }
                else
                {
                    BridgeLog($"<-- 400 Bad Request: Empty URL in POST payload (raw body: '{body}')");
                    context.Response.StatusCode = 400;
                    return;
                }
            }

            BridgeLog($"<-- 200 OK: Fallback handler for {method} {path}");
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            using (var fallbackWriter = new StreamWriter(context.Response.OutputStream))
            {
                await fallbackWriter.WriteAsync("{\"status\":\"ok\"}");
            }
        }
        catch (Exception ex)
        {
            BridgeLog($"<-- 500 Internal Server Error: {ex.Message}");
            try { context.Response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    public static string SanitizeFileName(string? rawFileName)
    {
        return UrlMetadataInspector.CleanFileName(rawFileName);
    }

    private void EnqueueBrowserPrompt(BrowserPromptItem item)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            // Deduplicate: If prompt is currently displaying or queued for the exact same URL, ignore duplicate trigger
            if (_activePromptUrl == item.Url || _browserPromptQueue.Any(p => p.Url == item.Url))
            {
                return;
            }

            _browserPromptQueue.Enqueue(item);
            if (!_isPromptShowing)
            {
                await ProcessNextBrowserPromptAsync();
            }
        });
    }

    private async Task ProcessNextBrowserPromptAsync()
    {
        _isPromptShowing = true;
        try
        {
            while (_browserPromptQueue.TryDequeue(out var nextItem))
            {
                _activePromptUrl = nextItem.Url;
                try
                {
                    if (await TryContinueUnfinishedDownloadAsync(nextItem))
                    {
                        continue;
                    }

                    if (AppSettings.Current.AutoStartBrowserDownloads)
                    {
                        await AutoStartDownloadFromBrowserAsync(nextItem);
                    }
                    else
                    {
                        await PromptNewDownloadFromBrowserAsync(
                            nextItem.Url,
                            nextItem.FileName,
                            nextItem.Size,
                            nextItem.Cookies,
                            nextItem.Referrer,
                            nextItem.UserAgent,
                            nextItem.PageUrl);
                    }
                }
                finally
                {
                    _activePromptUrl = null;
                }
            }
        }
        finally
        {
            _isPromptShowing = false;
            _activePromptUrl = null;
        }
    }

    /// <summary>
    /// A fresh capture of a file that stopped half-way (e.g. its signed link expired) continues that task
    /// instead of starting over. Returns true when the capture was fully handled here.
    /// </summary>
    private async Task<bool> TryContinueUnfinishedDownloadAsync(BrowserPromptItem item)
    {
        var match = ExpiredLinkMatcher.FindMatch(_viewModel.Engine.Tasks, item.FileName, item.Size);
        if (match == null) return false;

        match.RecalculateDownloadedBytes();
        bool resume = ConfirmDialog.Ask(
            IsVisible ? this : null,
            "Yarım kalan indirme bulundu",
            $"{match.FileName} %{match.ProgressPercentage:F0}'de kaldı. Yeni bağlantıyla kaldığı yerden devam edilsin mi?",
            $"{DownloadTask.FormatBytes(match.DownloadedBytes)} / {DownloadTask.FormatBytes(match.TotalBytes)}",
            "Kaldığı yerden devam et",
            "");
        if (!resume) return false;

        string? reason;
        try
        {
            reason = await _viewModel.Engine.RefreshTaskUrlAsync(match.Id, item.Url, item.Cookies, item.Referrer, item.UserAgent);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        if (reason == null)
        {
            BridgeLog($"[Resume] Task {match.Id} continues with a fresh link: '{item.Url}'");
            _trayIcon?.ShowNotification("⚡ VortexDownloader", $"Kaldığı yerden devam ediyor: {match.FileName}");
            return true;
        }

        BridgeLog($"[Resume] Fresh link rejected for task {match.Id}: {reason}");
        // Fall back to the normal flow; cancelling here drops the capture.
        return !ConfirmDialog.Ask(
            IsVisible ? this : null,
            "Kaldığı yerden devam edilemedi",
            reason,
            match.FileName,
            "Yeni indirme olarak ekle",
            "");
    }

    private async Task AutoStartDownloadFromBrowserAsync(BrowserPromptItem item)
    {
        try
        {
            string targetDir = AppSettings.Current.DefaultDownloadDirectory;
            if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
            {
                targetDir = AppSettings.GetDefaultDownloadPath();
            }

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string? rawName = item.FileName;
            if (string.IsNullOrWhiteSpace(rawName))
            {
                if (Uri.TryCreate(item.Url, UriKind.Absolute, out var uri))
                {
                    rawName = Path.GetFileName(uri.AbsolutePath);
                }
            }

            string safeFileName = SanitizeFileName(rawName);
            if (string.IsNullOrWhiteSpace(safeFileName))
            {
                safeFileName = "download.bin";
            }

            string targetPath = Path.Combine(targetDir, safeFileName);
            var activePaths = _viewModel.Downloads.Select(d => d.DestinationPath);
            string destinationPath = PathHelper.GetUniqueDestinationPath(targetPath, activePaths);

            BridgeLog($"[Auto-Start] Direct initiating download for '{item.Url}' -> '{destinationPath}' (Chunks: {AppSettings.Current.DefaultChunkCount})");

            var task = await _viewModel.Engine.AddDownloadAsync(
                item.Url,
                destinationPath,
                AppSettings.Current.DefaultChunkCount,
                autoStart: true,
                cookies: item.Cookies,
                referrer: item.Referrer,
                userAgent: item.UserAgent,
                sourcePageUrl: item.PageUrl);

            _trayIcon?.ShowNotification("⚡ VortexDownloader", $"İndirme Başlatıldı: {task.FileName}");
            BridgeLog($"[Auto-Start] Download started successfully: TaskId={task.Id}, File='{task.FileName}', Size={task.TotalBytes} bytes");
        }
        catch (Exception ex)
        {
            BridgeLog($"[Auto-Start ERROR] Failed to auto-start download for '{item.Url}': {ex.Message}");
            _trayIcon?.ShowNotification("⚠️ VortexDownloader", $"İndirme Başlatılamadı: {ex.Message}");
        }
    }

    private async Task PromptNewDownloadFromBrowserAsync(
        string url,
        string? fileName,
        long? size,
        string? cookies = null,
        string? referrer = null,
        string? userAgent = null,
        string? pageUrl = null)
    {
        var prompt = new DownloadPromptWindow(_viewModel.Engine, url, fileName, size, cookies, referrer, userAgent)
        {
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        prompt.ContentRendered += (_, _) =>
        {
            try
            {
                WindowHelper.BringToFront(prompt);
            }
            catch { }
        };

        if (prompt.ShowDialog() == true)
        {
            if (prompt.DownloadWithBrowser)
            {
                _fallbackBrowserUrls[url] = DateTime.UtcNow.AddSeconds(30);
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    BridgeLog($"Failed to open fallback URL in browser: {ex.Message}");
                }
                _trayIcon?.ShowNotification("🌐 VortexDownloader", $"İndirme tarayıcıya devredildi: {prompt.ResultFileName}");
                return;
            }

            try
            {
                var task = await _viewModel.Engine.AddDownloadAsync(
                    prompt.ResultUrl,
                    prompt.ResultDestinationPath,
                    prompt.ResultChunkCount,
                    autoStart: prompt.StartImmediately,
                    preloadedMetadata: prompt.CachedMetadata,
                    cookies: prompt.Cookies,
                    referrer: prompt.Referrer,
                    userAgent: prompt.UserAgent,
                    sourcePageUrl: pageUrl);

                _trayIcon?.ShowNotification("⚡ VortexDownloader", $"İndirme Başlatıldı: {task.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"İndirme başlatılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // Files picked inside a remote ZIP: one card per file in the main list, fetched one after another with Range requests
    private async Task DownloadZipEntriesAsync(string zipUrl, List<ZipEntryMetadata> entries, string folder)
    {
        // One token per card so deleting one file's card stops only that file
        var tokens = entries.Select(_ => new CancellationTokenSource()).ToList();
        var cards = entries.Select((entry, n) => new DownloadItemViewModel
        {
            Id = Guid.NewGuid(),
            FileName = Path.GetFileName(entry.FileName.TrimEnd('/')),
            Url = zipUrl,
            DestinationPath = Path.Combine(folder, Path.GetFileName(entry.FileName.TrimEnd('/'))),
            Status = DownloadStatus.Queued,
            CustomStatusText = "Sırada (ZIP)",
            ChunkInfo = "ZIP içinden",
            SupportsPause = false,
            TotalBytes = entry.UncompressedSize,
            CancelAction = () => { try { tokens[n].Cancel(); } catch { } }
        }).ToList();
        foreach (var card in Enumerable.Reverse(cards)) _viewModel.Downloads.Insert(0, card);
        _viewModel.UpdateAggregates();

        using var client = ChunkDownloader.CreateOptimizedHttpClient();
        for (int i = 0; i < entries.Count; i++)
        {
            var (entry, card) = (entries[i], cards[i]);
            var token = tokens[i].Token;
            if (token.IsCancellationRequested || !_viewModel.Downloads.Contains(card)) continue; // deleted from the list meanwhile
            card.Status = DownloadStatus.Downloading;
            card.CustomStatusText = "İndiriliyor (ZIP)";
            var clock = Stopwatch.StartNew();
            var progress = new Progress<long>(written =>
            {
                card.DownloadedBytes = written;
                card.ProgressPercentage = entry.UncompressedSize > 0 ? written * 100.0 / entry.UncompressedSize : 0;
                double seconds = Math.Max(0.001, clock.Elapsed.TotalSeconds);
                card.SpeedBytesPerSec = written / seconds;
                card.FormattedSpeed = $"{DownloadTask.FormatBytes((long)(written / seconds))}/s";
            });
            try
            {
                string path = await ZipEntryExtractor.ExtractRemoteEntryAsync(client, zipUrl, entry, folder, progress, token);
                card.DestinationPath = path; // may carry a " (1)" suffix
                card.FileName = Path.GetFileName(path);
                card.DownloadedBytes = card.TotalBytes;
                card.ProgressPercentage = 100;
                card.SpeedBytesPerSec = 0;
                card.FormattedSpeed = "Tamamlandı";
                card.CustomStatusText = "Tamamlandı";
                card.Status = DownloadStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                card.Status = DownloadStatus.Canceled;
                card.CustomStatusText = "İptal edildi";
                card.FormattedSpeed = "";
            }
            catch (Exception ex)
            {
                card.Status = DownloadStatus.Failed;
                card.CustomStatusText = "Hata";
                card.ErrorMessage = ex.Message;
                card.FormattedSpeed = "";
                Log($"ZIP entry download failed '{entry.FileName}': {ex.Message}");
            }
            _viewModel.UpdateAggregates();
        }
        tokens.ForEach(t => t.Dispose());
    }

    private async void BtnNewDownload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewDownloadDialog(_viewModel.Engine)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            if (dialog.ZipRequest is { } zip)
            {
                _ = DownloadZipEntriesAsync(zip.Url, zip.Entries, zip.Folder);
                return;
            }
            try
            {
                if (dialog.BatchItems.Count > 1)
                {
                    var failedItems = new List<string>();
                    foreach (var item in dialog.BatchItems)
                    {
                        try
                        {
                            await _viewModel.Engine.AddDownloadAsync(
                                item.Url,
                                item.DestinationPath,
                                dialog.ResultChunkCount,
                                autoStart: true);
                        }
                        catch (Exception ex)
                        {
                            failedItems.Add($"{item.Url}: {ex.Message}");
                        }
                    }

                    if (failedItems.Count > 0)
                    {
                        MessageBox.Show(this, $"{dialog.BatchItems.Count - failedItems.Count} indirme eklendi.\n{failedItems.Count} bağlantı eklenemedi:\n" + string.Join("\n", failedItems.Take(5)), "Toplu İndirme Bilgisi", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(dialog.ResultUrl))
                {
                    await _viewModel.Engine.AddDownloadAsync(
                        dialog.ResultUrl,
                        dialog.ResultDestinationPath,
                        dialog.ResultChunkCount,
                        autoStart: true,
                        mirrorUrls: dialog.ResultMirrorUrls);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"İndirme başlatılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ReloadPlugins()
    {
        _pluginCatalog = new PluginCatalog(_pluginStore.LoadAll());
        var favorites = AppSettings.Current.FavoritePlugins;
        _viewModel.PluginCards = _pluginCatalog.Plugins
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PluginCardViewModel(p, favorites.Contains(p.Id),
                () => OpenPlugin(p), ToggleFavoritePlugin, RemovePlugin))
            .ToList();
    }

    private void OpenPlugin(IVortexPlugin plugin)
    {
        if (plugin is not IMediaProviderPlugin media) return;
        MediaPluginPanel.Initialize(_viewModel.Engine, media);
        MediaPluginPanel.OpenWithAnimation();
    }

    private void ToggleFavoritePlugin(PluginCardViewModel card)
    {
        card.IsFavorite = !card.IsFavorite;
        var favorites = AppSettings.Current.FavoritePlugins;
        favorites.Remove(card.Id);
        if (card.IsFavorite) favorites.Add(card.Id);
        AppSettings.Current.Save();
        _viewModel.RefreshFavorites();
    }

    private void RemovePlugin(PluginCardViewModel card)
    {
        if (MessageBox.Show(this, $"\"{card.Name}\" eklentisi kaldırılsın mı?", "Eklentiyi Kaldır",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { _pluginStore.Remove(card.Id); }
        catch (Exception ex) { MessageBox.Show(this, $"Eklenti kaldırılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        AppSettings.Current.FavoritePlugins.Remove(card.Id);
        AppSettings.Current.Save();
        ReloadPlugins();
    }

    private async void BtnInstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        string text = TxtPluginUrl.Text.Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url)) { MessageBox.Show(this, "Geçerli bir bağlantı girin.", "Eklenti Ekle", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        BtnInstallPlugin.IsEnabled = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var manifest = await PluginStore.FetchAsync(http, url);
            var existing = _pluginCatalog.Plugins.FirstOrDefault(p => p.Id == manifest.Id);
            string action = existing is null ? "kurulsun" : $"v{existing.Version} yerine v{manifest.Version} sürümüne güncellensin";
            var answer = MessageBox.Show(this,
                $"\"{manifest.Name}\" v{manifest.Version} {action} mı?\n\nBu eklenti kod çalıştırmaz; yalnızca şu sitelerin bağlantılarını Vortex medya indiriciye yönlendirir:\n{string.Join(", ", manifest.Hosts)}",
                "Eklenti Ekle", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            _pluginStore.Install(manifest);
            ReloadPlugins();
            TxtPluginUrl.Clear();
        }
        catch (Exception ex) when (ex is FormatException or HttpRequestException or TaskCanceledException or IOException)
        {
            MessageBox.Show(this, $"Eklenti eklenemedi: {ex.Message}", "Eklenti Ekle", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { BtnInstallPlugin.IsEnabled = true; }
    }

    private void ExecuteMediaDownload(MediaDownloadOptions options, bool startImmediately)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(options.Url) || string.IsNullOrWhiteSpace(options.DestinationPath))
            {
                return;
            }

            if (!startImmediately)
            {
                var queuedItem = new DownloadItemViewModel
                {
                    Id = Guid.NewGuid(),
                    FileName = Path.GetFileName(options.DestinationPath),
                    Url = options.Url,
                    DestinationPath = options.DestinationPath,
                    Status = DownloadStatus.Queued,
                    CustomStatusText = "YouTube kuyruğunda",
                    ChunkInfo = $"{options.ConcurrentFragments} Akış / Parça"
                };
                queuedItem.StartAction = () => Dispatcher.Invoke(() =>
                {
                    if (!_viewModel.Downloads.Contains(queuedItem)) return;
                    _viewModel.Downloads.Remove(queuedItem);
                    ExecuteMediaDownload(options, startImmediately: true);
                    MediaPluginPanel.CloseWithAnimation();
                });
                queuedItem.CancelAction = () => Dispatcher.Invoke(() =>
                {
                    _viewModel.Downloads.Remove(queuedItem);
                    _viewModel.UpdateAggregates();
                });
                _viewModel.Downloads.Insert(0, queuedItem);
                _viewModel.UpdateAggregates();
                return;
            }

            var cts = new CancellationTokenSource();
            var downloadItem = new DownloadItemViewModel
            {
                Id = Guid.NewGuid(),
                FileName = Path.GetFileName(options.DestinationPath),
                Url = options.Url,
                DestinationPath = options.DestinationPath,
                Status = DownloadStatus.Downloading,
                CustomStatusText = "İndiriliyor (yt-dlp)",
                ChunkInfo = $"{options.ConcurrentFragments} Akış / Parça",
                FormattedSpeed = "Başlatılıyor...",
                ProgressPercentage = 0,
                CancelAction = () =>
                {
                    try { cts.Cancel(); } catch { }
                }
            };

            _viewModel.Downloads.Insert(0, downloadItem);
            _viewModel.UpdateAggregates();

            var progressReporter = new Progress<MediaProgressInfo>(info =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (info.Status == MediaDownloadStatus.Downloading)
                    {
                        downloadItem.Status = DownloadStatus.Downloading;
                        downloadItem.CustomStatusText = "İndiriliyor (yt-dlp)";
                        downloadItem.ProgressPercentage = info.Percentage;
                        if (info.TotalBytes > 0) downloadItem.TotalBytes = info.TotalBytes;
                        if (info.DownloadedBytes > 0) downloadItem.DownloadedBytes = info.DownloadedBytes;
                        if (!string.IsNullOrEmpty(info.SpeedText))
                        {
                            downloadItem.FormattedSpeed = !string.IsNullOrEmpty(info.EtaText)
                                ? $"{info.SpeedText} (ETA: {info.EtaText})"
                                : info.SpeedText;
                        }
                    }
                    else if (info.Status == MediaDownloadStatus.Remuxing)
                    {
                        downloadItem.Status = DownloadStatus.Assembling;
                        downloadItem.CustomStatusText = "Birleştiriliyor (FFmpeg)";
                        downloadItem.ProgressPercentage = 99.0;
                        downloadItem.FormattedSpeed = string.IsNullOrWhiteSpace(info.SpeedText) ? "Dönüştürülüyor..." : info.SpeedText;
                    }
                    else if (info.Status == MediaDownloadStatus.Completed)
                    {
                        downloadItem.Status = DownloadStatus.Completed;
                        downloadItem.CustomStatusText = "Tamamlandı";
                        downloadItem.ProgressPercentage = 100.0;
                        downloadItem.FormattedSpeed = "Tamamlandı";
                        if (File.Exists(options.DestinationPath))
                        {
                            try
                            {
                                var fi = new FileInfo(options.DestinationPath);
                                downloadItem.TotalBytes = fi.Length;
                                downloadItem.DownloadedBytes = fi.Length;
                            }
                            catch { }
                        }
                        _viewModel.UpdateAggregates();
                    }
                    else if (info.Status == MediaDownloadStatus.Error)
                    {
                        downloadItem.Status = DownloadStatus.Failed;
                        downloadItem.CustomStatusText = "Hata";
                        downloadItem.ErrorMessage = info.SpeedText;
                        _viewModel.UpdateAggregates();
                    }
                });
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    var sidecar = new SidecarManager();
                    if (sidecar.GetYtDlpPath() == null || sidecar.GetFfmpegPath() == null)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            downloadItem.CustomStatusText = "Bileşenler İndiriliyor...";
                        });
                        await sidecar.EnsureSidecarsAsync(ct: cts.Token);
                    }

                    var ytService = new YtDlpService(sidecar);
                    await ytService.DownloadMediaAsync(options, progressReporter, cts.Token);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        downloadItem.Status = DownloadStatus.Completed;
                        downloadItem.CustomStatusText = "Tamamlandı";
                        downloadItem.ProgressPercentage = 100.0;
                        downloadItem.FormattedSpeed = "Tamamlandı";
                        if (File.Exists(options.DestinationPath))
                        {
                            try
                            {
                                var fi = new FileInfo(options.DestinationPath);
                                downloadItem.TotalBytes = fi.Length;
                                downloadItem.DownloadedBytes = fi.Length;
                            }
                            catch { }
                        }
                        _viewModel.UpdateAggregates();
                        _trayIcon?.ShowNotification("⚡ VortexDownloader", $"Medya İndirildi: {options.DestinationPath}");
                    });
                }
                catch (OperationCanceledException)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        downloadItem.Status = DownloadStatus.Canceled;
                        downloadItem.CustomStatusText = "İptal Edildi";
                        _viewModel.UpdateAggregates();
                    });
                }
                catch (Exception ex)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        downloadItem.Status = DownloadStatus.Failed;
                        downloadItem.CustomStatusText = "Hata";
                        downloadItem.ErrorMessage = ex.Message;
                        _viewModel.UpdateAggregates();
                        MessageBox.Show(this, $"Medya indirme başarısız oldu:\n{ex.Message}", "Medya İndirme Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }
                finally
                {
                    cts.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Medya indirme başlatılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        string nextTheme = ThemeManager.CurrentTheme == "Dark" ? "Light" : "Dark";
        ThemeManager.SetTheme(nextTheme);
    }

    private void BtnToolbarResume_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.StartAllCommand.Execute(null);
    }

    private void BtnToolbarStop_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.PauseAllCommand.Execute(null);
    }

    private void BtnToolbarClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearCompletedCommand.Execute(null);
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow
        {
            Owner = this
        };
        if (settingsWindow.ShowDialog() == true)
        {
            _viewModel.Engine.MaxConcurrentDownloads = AppSettings.Current.MaxConcurrentDownloads;
            _viewModel.Engine.ScanWithDefender = AppSettings.Current.ScanWithDefender;
            _viewModel.ShutdownWhenCompleted = AppSettings.Current.ShutdownWhenCompleted;
            _viewModel.SelectedMaxConcurrent = AppSettings.Current.MaxConcurrentDownloads switch
            {
                1 => "1 Görev",
                2 => "2 Görev",
                3 => "3 Görev",
                5 => "5 Görev",
                0 => "Sınırsız",
                _ => $"{AppSettings.Current.MaxConcurrentDownloads} Görev"
            };
        }
    }

    private void BtnToolbarSettings_Click(object sender, RoutedEventArgs e)
    {
        BtnSettings_Click(sender, e);
    }

    private void BtnToolbarScheduler_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "İndirme Zamanlayıcı ve Kuyruk Yöneticisi aktif.", "Zamanlayıcı", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnToolbarGrabber_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Site ve Medya Yakalayıcı (Grabber) hazır.", "Medya Yakalayıcı", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
