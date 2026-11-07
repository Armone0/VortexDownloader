using System.IO;
using System.Windows;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
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

    protected override void OnExit(ExitEventArgs e)
    {
        Log($"OnExit entered with code {e.ApplicationExitCode}");
        base.OnExit(e);
    }
}
