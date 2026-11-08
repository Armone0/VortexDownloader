using System.IO;
using System.Text.Json;

namespace VortexDownloader.UI.Services;

public class AppSettings
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VortexDownloader");
    private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

    private static AppSettings? _current;
    private static readonly object _lock = new();

    public static AppSettings Current
    {
        get
        {
            if (_current == null)
            {
                lock (_lock)
                {
                    _current ??= Load();
                }
            }
            return _current;
        }
    }

    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "tr";
    public string DefaultDownloadDirectory { get; set; } = GetDefaultDownloadPath();
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int DefaultChunkCount { get; set; } = 8;
    public bool ShutdownWhenCompleted { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
    public bool AutoStartBrowserDownloads { get; set; } = true;

    public static string GetDefaultDownloadPath()
    {
        string userDownloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(userDownloads))
        {
            return userDownloads;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                string json = File.ReadAllText(SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    if (string.IsNullOrWhiteSpace(settings.DefaultDownloadDirectory) || !Directory.Exists(settings.DefaultDownloadDirectory))
                    {
                        settings.DefaultDownloadDirectory = GetDefaultDownloadPath();
                    }
                    return settings;
                }
            }
        }
        catch
        {
            // Fallback to default values on any error
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            if (!Directory.Exists(SettingsFolder))
            {
                Directory.CreateDirectory(SettingsFolder);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Suppress I/O errors during save
        }
    }
}
