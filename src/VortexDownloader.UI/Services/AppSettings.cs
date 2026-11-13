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
    public bool AutoStartBrowserDownloads { get; set; } = false;
    public bool ScanWithDefender { get; set; } = true;
    public bool GameModeEnabled { get; set; } = true;
    public int GameModeLimitKBps { get; set; } = 512;
    public List<string> FavoritePlugins { get; set; } = [];
    /// <summary>New downloads into the default folder go to a subfolder by type (Video, Müzik, …). Existing files are never moved.</summary>
    public bool AutoFolderByType { get; set; } = false;

    // Update check: only asks GitHub for the latest release number; never downloads or runs anything.
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }
    /// <summary>Newest version seen on GitHub ("1.9.2"), so the notice survives restarts inside the 24 h window.</summary>
    public string? LatestKnownVersion { get; set; }
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Offer copied direct-file links as downloads (always asks first).</summary>
    public bool WatchClipboardLinks { get; set; } = false;
    /// <summary>System (Windows proxy), None or Manual (<see cref="Core.Network.ProxyMode"/>). Applied at startup.</summary>
    public string ProxyMode { get; set; } = "System";
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; } = 8080;
    public string ProxyUser { get; set; } = "";
    /// <summary>DPAPI blob (current user) of the proxy password; never stored in plain text.</summary>
    public string? ProxyPasswordProtected { get; set; }

    // Browser bridge pairing: random per-install secret + the extension origins the user approved
    public string BridgeToken { get; set; } = "";
    public List<string> PairedExtensionOrigins { get; set; } = [];

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
