using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using VortexDownloader.Core.Engine;
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
    private bool _isRealExit;

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
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
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

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Log($"MainWindow_Closing enter (_isRealExit={_isRealExit})");
        if (!_isRealExit)
        {
            // Minimize to tray instead of quitting
            e.Cancel = true;
            Hide();
            Log("MainWindow_Closing minimized to tray (cancelled exit)");
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
            _extensionBridgeListener.Prefixes.Add("http://127.0.0.1:19741/download/");
            _extensionBridgeListener.Start();

            _bridgeCts = new CancellationTokenSource();
            _ = Task.Run(() => BridgeListenLoopAsync(_bridgeCts.Token));
        }
        catch
        {
            // Port might be in use or permission restricted; non-fatal
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
        try
        {
            // Security 1: Host header validation (Defend against DNS rebinding attacks)
            string? host = context.Request.Headers["Host"];
            if (string.IsNullOrEmpty(host) || (!host.StartsWith("127.0.0.1") && !host.StartsWith("localhost")))
            {
                context.Response.StatusCode = 403;
                return;
            }

            // Security 2: Sec-Fetch-Site validation (Block cross-site web page requests)
            string? secFetchSite = context.Request.Headers["Sec-Fetch-Site"];
            if (secFetchSite == "cross-site")
            {
                context.Response.StatusCode = 403;
                return;
            }

            // Security 3: Origin validation (Only allow recognized extension schemes)
            string? origin = context.Request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
            {
                bool isExtension = origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                                   origin.StartsWith("edge-extension://", StringComparison.OrdinalIgnoreCase) ||
                                   origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase);
                if (!isExtension)
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                context.Response.Headers.Add("Access-Control-Allow-Origin", origin);
            }
            else
            {
                context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            }

            context.Response.Headers.Add("Access-Control-Allow-Methods", "POST, OPTIONS");
            context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, X-Vortex-Auth");

            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 200;
                return;
            }

            // Security 4: Shared auth token header verification between extension and desktop bridge
            string? authToken = context.Request.Headers["X-Vortex-Auth"];
            if (authToken != "vortex-bridge-extension-auth-token")
            {
                context.Response.StatusCode = 403;
                return;
            }

            if (context.Request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                string body = await reader.ReadToEndAsync();
                var data = JsonSerializer.Deserialize<BrowserDownloadPayload>(body);

                if (data != null && !string.IsNullOrWhiteSpace(data.Url))
                {
                    // Security: Enforce HTTP/HTTPS scheme validation to prevent file:// or command injection URIs
                    if (!Uri.TryCreate(data.Url, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    string safeFileName = SanitizeFileName(data.FileName);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        RestoreFromTray();
                        PromptNewDownloadFromBrowser(uri.AbsoluteUri, safeFileName, data.FileSize);
                    });
                }
            }

            context.Response.StatusCode = 200;
            using var writer = new StreamWriter(context.Response.OutputStream);
            await writer.WriteAsync("{\"status\":\"ok\"}");
        }
        catch
        {
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

    private async void PromptNewDownloadFromBrowser(string url, string? fileName, long? size)
    {
        var prompt = new DownloadPromptWindow(_viewModel.Engine, url, fileName, size)
        {
            Owner = this
        };

        if (prompt.ShowDialog() == true)
        {
            try
            {
                await _viewModel.Engine.AddDownloadAsync(
                    prompt.ResultUrl,
                    prompt.ResultDestinationPath,
                    prompt.ResultChunkCount,
                    autoStart: prompt.StartImmediately);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"İndirme başlatılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    foreach (var item in dialog.BatchItems)
                    {
                        await _viewModel.Engine.AddDownloadAsync(
                            item.Url,
                            item.DestinationPath,
                            dialog.ResultChunkCount,
                            autoStart: true);
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

    private record BrowserDownloadPayload(string Url, string? FileName, long? FileSize);
}
