using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Models;
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
    private VortexTrayIcon? _trayIcon;
    private HttpListener? _extensionBridgeListener;
    private CancellationTokenSource? _bridgeCts;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private bool _isRealExit;

    private record BrowserPromptItem(
        string Url,
        string? FileName,
        long? Size,
        string? Cookies,
        string? Referrer,
        string? UserAgent);

    private readonly Queue<BrowserPromptItem> _browserPromptQueue = new();
    private bool _isPromptShowing = false;
    private string? _activePromptUrl = null;

    public MainWindow()
    {
        Log("MainWindow constructor start");
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        ThemeManager.ThemeChanged += OnThemeChanged;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;

        _trayIcon = new VortexTrayIcon(
            this,
            RestoreFromTray,
            () => _viewModel.Engine.StartAll(),
            () => _ = _viewModel.Engine.PauseAllAsync(),
            RealExit);

        StartBrowserExtensionBridge();
        Log("MainWindow constructor end");
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
        var settings = AppSettings.Current;
        _viewModel.Engine.MaxConcurrentDownloads = settings.MaxConcurrentDownloads;
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
            if (string.IsNullOrEmpty(host) || (!host.StartsWith("127.0.0.1") && !host.StartsWith("localhost")))
            {
                BridgeLog($"<-- 403 Forbidden: Invalid host header '{host}'");
                context.Response.StatusCode = 403;
                return;
            }

            // Security 2: Origin validation & CORS setup
            if (!string.IsNullOrEmpty(origin))
            {
                bool isExtension = origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                                   origin.StartsWith("edge-extension://", StringComparison.OrdinalIgnoreCase) ||
                                   origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase);
                context.Response.Headers.Add("Access-Control-Allow-Origin", isExtension ? origin : "*");
            }
            else
            {
                context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
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

            // Ping / Health Check Endpoint for Extension Status
            if (context.Request.HttpMethod == "GET")
            {
                BridgeLog("<-- 200 OK: Ping Health Check");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                using (var pingWriter = new StreamWriter(context.Response.OutputStream))
                {
                    await pingWriter.WriteAsync("{\"status\":\"online\",\"version\":\"1.4.0\"}");
                }
                return;
            }

            // Security: Shared auth token header verification between extension and desktop bridge
            if (authToken != "vortex-bridge-extension-auth-token")
            {
                BridgeLog($"<-- 403 Forbidden: Auth token mismatch (got '{authToken}')");
                context.Response.StatusCode = 403;
                return;
            }

            if (context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();
                BridgeLog($"--> [POST] Body ({body.Length} chars): {body}");
                var data = JsonSerializer.Deserialize<BrowserDownloadPayload>(body, JsonOptions);

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
                        data?.UserAgent));

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
                    if (AppSettings.Current.AutoStartBrowserDownloads)
                    {
                        await AutoStartDownloadFromBrowserAsync(nextItem);
                    }
                    else
                    {
                        RestoreFromTray();
                        await PromptNewDownloadFromBrowserAsync(
                            nextItem.Url,
                            nextItem.FileName,
                            nextItem.Size,
                            nextItem.Cookies,
                            nextItem.Referrer,
                            nextItem.UserAgent);
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
                userAgent: item.UserAgent);

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
        string? userAgent = null)
    {
        var prompt = new DownloadPromptWindow(_viewModel.Engine, url, fileName, size, cookies, referrer, userAgent)
        {
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };

        if (IsVisible)
        {
            try { prompt.Owner = this; } catch { }
        }

        WindowHelper.BringToFrontAndFocus(prompt);
        prompt.Loaded += (_, _) => WindowHelper.BringToFrontAndFocus(prompt);

        if (prompt.ShowDialog() == true)
        {
            try
            {
                await _viewModel.Engine.AddDownloadAsync(
                    prompt.ResultUrl,
                    prompt.ResultDestinationPath,
                    prompt.ResultChunkCount,
                    autoStart: prompt.StartImmediately,
                    preloadedMetadata: prompt.CachedMetadata,
                    cookies: prompt.Cookies,
                    referrer: prompt.Referrer,
                    userAgent: prompt.UserAgent);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"İndirme başlatılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void BtnNewDownload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewDownloadDialog(_viewModel.Engine)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
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
                        autoStart: true);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"İndirme başlatılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
