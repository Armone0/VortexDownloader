using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
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
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

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
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        }
        catch
        {
        }
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
        await _viewModel.InitializeAsync();
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isRealExit)
        {
            // Minimize to tray instead of quitting
            e.Cancel = true;
            Hide();
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
            // Security: Prevent malicious webpage CSRF drive-by downloads.
            // Check the Origin header: only allow browser extensions or local requests without web Origin.
            string? origin = context.Request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
            {
                bool isExtension = origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
                                   origin.StartsWith("edge-extension://", StringComparison.OrdinalIgnoreCase) ||
                                   origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase);
                if (!isExtension)
                {
                    // Block web origins attempting drive-by downloads
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
            context.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 200;
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
        if (string.IsNullOrWhiteSpace(rawFileName)) return "download.bin";
        string fileName = Path.GetFileName(rawFileName.Trim());
        char[] invalid = Path.GetInvalidFileNameChars();
        foreach (char c in invalid)
        {
            fileName = fileName.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(fileName) ? "download.bin" : fileName;
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

    private record BrowserDownloadPayload(string Url, string? FileName, long? FileSize);
}
