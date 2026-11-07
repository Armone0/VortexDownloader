using System.Windows;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
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
    }
}
