using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Network;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;

    private static void Log(string msg)
    {
        try
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VortexDownloader");
            Directory.CreateDirectory(appData);
            File.AppendAllText(Path.Combine(appData, "startup.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log("OnStartup entered");

        const string mutexName = "VortexDownloader_SingleInstance_Mutex_19741";
        try
        {
            _singleInstanceMutex = new Mutex(true, mutexName, out bool isNewInstance);
            if (!isNewInstance)
            {
                Log("Another instance is already running. Restoring running instance and shutting down.");
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    _ = client.GetAsync("http://127.0.0.1:19741/restore").Result;
                }
                catch { }

                Shutdown();
                return;
            }
        }
        catch (Exception ex)
        {
            Log($"Mutex error: {ex.Message}");
        }

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            Log($"UnhandledException: {args.ExceptionObject}");
        };

        DispatcherUnhandledException += (s, args) =>
        {
            Log($"DispatcherUnhandledException: {args.Exception}");
        };

        AppDomain.CurrentDomain.ProcessExit += (s, args) =>
        {
            Log($"ProcessExit called. Environment.ExitCode = {Environment.ExitCode}");
        };

        base.OnStartup(e);

        var settings = AppSettings.Current;
        ApplyProxy(settings);
        if (!string.IsNullOrEmpty(settings.Theme) && !settings.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase))
        {
            ThemeManager.SetTheme(settings.Theme);
        }

        if (!string.IsNullOrEmpty(settings.Language) && !settings.Language.Equals("tr", StringComparison.OrdinalIgnoreCase))
        {
            LocalizationManager.SetLanguage(settings.Language);
        }

        Log("OnStartup finished");
    }

    // One switch for every HttpClient in the app: none sets its own Proxy, and handlers read DefaultProxy on their first request.
    private static void ApplyProxy(AppSettings settings)
    {
        try
        {
            if (Enum.TryParse<ProxyMode>(settings.ProxyMode, ignoreCase: true, out var mode) &&
                ProxyFactory.Create(mode, settings.ProxyHost, settings.ProxyPort, settings.ProxyUser,
                    SecretProtector.Unprotect(settings.ProxyPasswordProtected)) is { } proxy)
            {
                HttpClient.DefaultProxy = proxy;
                // yt-dlp runs as its own process and never sees DefaultProxy
                ProxyFactory.ChildProcessEnvironment = ProxyFactory.BuildChildEnvironment(mode, settings.ProxyHost, settings.ProxyPort,
                    settings.ProxyUser, SecretProtector.Unprotect(settings.ProxyPasswordProtected));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            Log($"Proxy setting ignored, using the system proxy: {ex.GetType().Name}"); // no host/user/password in the log
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log($"OnExit entered with code {e.ApplicationExitCode}");
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
