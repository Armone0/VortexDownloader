using System.Windows;

namespace VortexDownloader.UI.Services;

public static class LocalizationManager
{
    public static string CurrentLanguage { get; private set; } = "tr";

    public static event Action<string>? LanguageChanged;

    public static void SetLanguage(string langCode)
    {
        bool isEn = langCode.Equals("en", StringComparison.OrdinalIgnoreCase);
        string stringsFile = isEn ? "Strings.en.xaml" : "Strings.tr.xaml";

        var app = Application.Current;
        if (app != null)
        {
            var existingDict = app.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source != null && (
                    d.Source.OriginalString.Contains("Strings.tr.xaml") || 
                    d.Source.OriginalString.Contains("Strings.en.xaml")));

            var newDict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/VortexDownloader;component/Localization/{stringsFile}", UriKind.RelativeOrAbsolute)
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
        }

        CurrentLanguage = isEn ? "en" : "tr";
        LanguageChanged?.Invoke(CurrentLanguage);
    }

    public static string GetString(string key, string fallback = "")
    {
        try
        {
            if (Application.Current != null && Application.Current.TryFindResource(key) is string val)
            {
                return val;
            }
        }
        catch
        {
            // Ignore resource lookup errors
        }
        return fallback;
    }
}
