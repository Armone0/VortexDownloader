using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VortexDownloader.UI.Services;

public static class ThemeManager
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static string CurrentTheme { get; private set; } = "Dark";

    public static event Action<string>? ThemeChanged;

    public static void SetTheme(string themeName)
    {
        bool isDark = !themeName.Equals("Light", StringComparison.OrdinalIgnoreCase);
        string themeFile = isDark ? "DarkTheme.xaml" : "LightTheme.xaml";

        var app = Application.Current;
        if (app != null)
        {
            var existingDict = app.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source != null && (
                    d.Source.OriginalString.Contains("DarkTheme.xaml") || 
                    d.Source.OriginalString.Contains("LightTheme.xaml")));

            var newDict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/VortexDownloader;component/Themes/{themeFile}", UriKind.RelativeOrAbsolute)
            };

            if (existingDict != null)
            {
                int index = app.Resources.MergedDictionaries.IndexOf(existingDict);
                app.Resources.MergedDictionaries[index] = newDict;
            }
            else
            {
                app.Resources.MergedDictionaries.Add(newDict);
            }

            foreach (Window window in app.Windows)
            {
                ApplyDwmDarkMode(window, isDark);
            }
        }

        CurrentTheme = isDark ? "Dark" : "Light";
        ThemeChanged?.Invoke(CurrentTheme);
    }

    public static void ApplyDwmDarkMode(Window window, bool isDark)
    {
        try
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle != IntPtr.Zero)
            {
                int darkMode = isDark ? 1 : 0;
                DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            }
        }
        catch
        {
            // Suppress DWM API failure on unsupported environments
        }
    }
}
