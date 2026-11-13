using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Models;
using VortexDownloader.UI.Services;

namespace VortexDownloader.UI.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly DownloadEngine _engine;
    private readonly SynchronizationContext? _uiContext;
    private DownloadItemViewModel? _selectedDownload;
    private string _totalSpeedFormatted = "0 KB/s";
    private int _activeDownloadsCount;
    private string _selectedSpeedLimit = "Sınırsız";

    public ObservableCollection<DownloadItemViewModel> Downloads { get; } = [];

    public DownloadEngine Engine => _engine;

    public DownloadItemViewModel? SelectedDownload
    {
        get => _selectedDownload;
        set => SetProperty(ref _selectedDownload, value);
    }

    private readonly Queue<double> _speedSamples = new();
    private long _lastSpeedSampleTick;
    private IReadOnlyList<double> _speedBars = [];

    public IReadOnlyList<double> SpeedBars
    {
        get => _speedBars;
        private set => SetProperty(ref _speedBars, value);
    }

    public string TotalSpeedFormatted
    {
        get => _totalSpeedFormatted;
        set => SetProperty(ref _totalSpeedFormatted, value);
    }

    public int ActiveDownloadsCount
    {
        get => _activeDownloadsCount;
        set
        {
            if (SetProperty(ref _activeDownloadsCount, value))
            {
                OnPropertyChanged(nameof(HasActiveDownloads));
            }
        }
    }

    public bool HasActiveDownloads => _activeDownloadsCount > 0;

    public bool HasIndeterminateProgress
    {
        get
        {
            var active = Downloads.Where(d => d.Status == DownloadStatus.Downloading).ToList();
            return active.Count > 0 && active.All(d => d.TotalBytes <= 0);
        }
    }

    public double TotalActiveProgressRatio
    {
        get
        {
            var active = Downloads.Where(d => d.Status == DownloadStatus.Downloading).ToList();
            if (active.Count == 0)
            {
                var paused = Downloads.Where(d => d.Status == DownloadStatus.Paused).ToList();
                if (paused.Count > 0)
                {
                    long pausedTotal = paused.Where(d => d.TotalBytes > 0).Sum(d => d.TotalBytes);
                    if (pausedTotal > 0)
                    {
                        long pausedDownloaded = paused.Where(d => d.TotalBytes > 0).Sum(d => d.DownloadedBytes);
                        return Math.Clamp((double)pausedDownloaded / pausedTotal, 0.0, 1.0);
                    }
                }
                return 0.0;
            }

            long totalBytes = active.Where(d => d.TotalBytes > 0).Sum(d => d.TotalBytes);
            if (totalBytes > 0)
            {
                long downloaded = active.Where(d => d.TotalBytes > 0).Sum(d => d.DownloadedBytes);
                return Math.Clamp((double)downloaded / totalBytes, 0.0, 1.0);
            }

            double avg = active.Average(d => d.ProgressPercentage);
            return Math.Clamp(avg / 100.0, 0.0, 1.0);
        }
    }

    public int TotalDownloadsCount => Downloads.Count;
    public bool HasNoDownloads => Downloads.Count == 0;
    public int PausedDownloadsCount => Downloads.Count(d => d.Status == DownloadStatus.Paused);
    public int CompletedDownloadsCount => Downloads.Count(d => d.Status == DownloadStatus.Completed);
    public int CompressedCount => Downloads.Count(d => HasExtension(d.FileName, ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2"));
    public int DocumentsCount => Downloads.Count(d => HasExtension(d.FileName, ".pdf", ".docx", ".doc", ".xlsx", ".txt", ".pptx", ".epub"));
    public int MusicCount => Downloads.Count(d => HasExtension(d.FileName, ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a"));
    public int ProgramsCount => Downloads.Count(d => HasExtension(d.FileName, ".exe", ".msi", ".iso", ".bat", ".cmd", ".apk"));
    public int VideoCount => Downloads.Count(d => HasExtension(d.FileName, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm"));
    public int FilteredDownloadsCount => FilteredDownloads.Count();

    public string FreeDiskSpaceText
    {
        get
        {
            try
            {
                string path = AppSettings.Current.DefaultDownloadDirectory;
                if (string.IsNullOrEmpty(path)) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string? root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    long freeGb = drive.AvailableFreeSpace / (1024L * 1024 * 1024);
                    return $"{root[0]}: {freeGb} GB Boş";
                }
            }
            catch { }
            return "Disk: Hazır";
        }
    }

    private string _currentFilter = "All";
    public string CurrentFilter
    {
        get => _currentFilter;
        set
        {
            if (SetProperty(ref _currentFilter, value))
            {
                OnPropertyChanged(nameof(FilterTitle));
                OnPropertyChanged(nameof(IsPluginsView));
                OnPropertyChanged(nameof(FilteredDownloads));
                OnPropertyChanged(nameof(FilteredDownloadsCount));
                OnPropertyChanged(nameof(IsAllFilter));
                OnPropertyChanged(nameof(IsDownloadingFilter));
                OnPropertyChanged(nameof(IsPausedFilter));
                OnPropertyChanged(nameof(IsCompletedFilter));
            }
        }
    }

    public bool IsPluginsView => _currentFilter == "Plugins";
    private IReadOnlyList<PluginCardViewModel> _pluginCards = [];

    public IReadOnlyList<PluginCardViewModel> PluginCards
    {
        get => _pluginCards;
        set
        {
            if (SetProperty(ref _pluginCards, value)) RefreshFavorites();
        }
    }

    public IEnumerable<PluginCardViewModel> FavoritePluginCards => _pluginCards.Where(p => p.IsFavorite);
    public bool HasFavoritePlugins => _pluginCards.Any(p => p.IsFavorite);

    public void RefreshFavorites()
    {
        OnPropertyChanged(nameof(FavoritePluginCards));
        OnPropertyChanged(nameof(HasFavoritePlugins));
    }

    public bool IsAllFilter => _currentFilter == "All";
    public bool IsDownloadingFilter => _currentFilter == "Downloading";
    public bool IsPausedFilter => _currentFilter == "Paused";
    public bool IsCompletedFilter => _currentFilter == "Completed";

    public string FilterTitle => _currentFilter switch
    {
        "Downloading" => "Aktif İndirmeler",
        "Paused" => "Duraklatılan İndirmeler",
        "Completed" => "Tamamlanan İndirmeler",
        "Compressed" => "Sıkıştırılmış Dosyalar (Arşivler)",
        "Documents" => "Belgeler",
        "Music" => "Müzik ve Ses",
        "Programs" => "Programlar ve Kurulumlar",
        "Video" => "Videolar",
        "Plugins" => "Eklentiler",
        "Grabber" => "Yakalayıcı Projeleri",
        "Queues" => "İndirme Kuyrukları",
        _ => "Tüm İndirmeler"
    };

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                OnPropertyChanged(nameof(FilteredDownloads));
                OnPropertyChanged(nameof(FilteredDownloadsCount));
            }
        }
    }

    public IEnumerable<DownloadItemViewModel> FilteredDownloads => string.IsNullOrWhiteSpace(_searchText)
        ? FilteredByCategory
        : FilteredByCategory.Where(d =>
            d.FileName.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
            d.Url.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<DownloadItemViewModel> FilteredByCategory => _currentFilter switch
    {
        "Downloading" => Downloads.Where(d => d.Status == DownloadStatus.Downloading),
        "Paused" => Downloads.Where(d => d.Status == DownloadStatus.Paused),
        "Completed" => Downloads.Where(d => d.Status == DownloadStatus.Completed),
        "Compressed" => Downloads.Where(d => HasExtension(d.FileName, ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2")),
        "Documents" => Downloads.Where(d => HasExtension(d.FileName, ".pdf", ".docx", ".doc", ".xlsx", ".txt", ".pptx", ".epub")),
        "Music" => Downloads.Where(d => HasExtension(d.FileName, ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a")),
        "Programs" => Downloads.Where(d => HasExtension(d.FileName, ".exe", ".msi", ".iso", ".bat", ".cmd", ".apk")),
        "Video" => Downloads.Where(d => HasExtension(d.FileName, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm")),
        _ => Downloads
    };

    private static bool HasExtension(string? fileName, params string[] exts)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return exts.Contains(ext);
    }

    public ICommand SetFilterCommand { get; }

    public string SelectedSpeedLimit
    {
        get => _selectedSpeedLimit;
        set
        {
            if (SetProperty(ref _selectedSpeedLimit, value))
            {
                ApplySpeedLimit(value);
            }
        }
    }

    public IReadOnlyList<string> SpeedLimitOptions { get; } = ["Sınırsız", "1 MB/s", "5 MB/s", "10 MB/s"];

    // Game / fullscreen mode: the user's own limit is kept separately so it survives the throttle.
    private long _userLimitBytes;
    private long _gameLimitBytes;
    private bool _gameModeActive;
    private string _gameModeLimitText = "";

    public bool IsGameModeActive
    {
        get => _gameModeActive;
        private set => SetProperty(ref _gameModeActive, value);
    }

    /// <summary>Effective limit shown in the game mode badge, e.g. "512 KB/s".</summary>
    public string GameModeLimitText
    {
        get => _gameModeLimitText;
        private set => SetProperty(ref _gameModeLimitText, value);
    }

    /// <summary>Called by the fullscreen poll; reads the settings each time so changes apply without a restart.</summary>
    public void UpdateGameMode(bool fullscreenAppRunning)
    {
        var settings = AppSettings.Current;
        bool active = settings.GameModeEnabled && settings.GameModeLimitKBps > 0 && fullscreenAppRunning;
        long gameLimit = settings.GameModeLimitKBps * 1024L;
        if (active == _gameModeActive && (!active || gameLimit == _gameLimitBytes)) return;

        _gameLimitBytes = gameLimit;
        IsGameModeActive = active;
        ApplyEffectiveLimit();
    }

    private void ApplyEffectiveLimit()
    {
        long effective = _gameModeActive
            ? SpeedLimitPolicy.CombineLimits(_userLimitBytes, _gameLimitBytes)
            : _userLimitBytes;

        _engine.SetGlobalSpeedLimit(effective);

        long kb = effective / 1024;
        GameModeLimitText = kb >= 1024 && kb % 1024 == 0 ? $"{kb / 1024} MB/s" : $"{kb} KB/s";
    }

    public IReadOnlyList<string> MaxConcurrentOptions { get; } = ["1 Görev", "2 Görev", "3 Görev", "5 Görev", "Sınırsız"];

    private string _selectedMaxConcurrent = "3 Görev";
    public string SelectedMaxConcurrent
    {
        get => _selectedMaxConcurrent;
        set
        {
            if (SetProperty(ref _selectedMaxConcurrent, value))
            {
                ApplyMaxConcurrent(value);
            }
        }
    }

    private void ApplyMaxConcurrent(string option)
    {
        int max = option switch
        {
            "1 Görev" => 1,
            "2 Görev" => 2,
            "3 Görev" => 3,
            "5 Görev" => 5,
            "Sınırsız" => 0,
            _ => 3
        };
        _engine.MaxConcurrentDownloads = max;
    }

    private bool _shutdownWhenCompleted;
    private bool _hasActiveDownloadsSession;
    private bool _shutdownTriggered;

    public bool ShutdownWhenCompleted
    {
        get => _shutdownWhenCompleted;
        set
        {
            if (SetProperty(ref _shutdownWhenCompleted, value))
            {
                if (value && ActiveDownloadsCount > 0)
                {
                    _hasActiveDownloadsSession = true;
                }
                CheckAutoShutdown();
            }
        }
    }

    internal Action? ShutdownAction { get; set; }

    private void CheckAutoShutdown()
    {
        if (!ShutdownWhenCompleted || _shutdownTriggered)
        {
            return;
        }

        if (ActiveDownloadsCount > 0)
        {
            _hasActiveDownloadsSession = true;
            return;
        }

        if (_hasActiveDownloadsSession && Downloads.Count > 0)
        {
            // Do not shutdown if any download failed
            bool anyFailed = Downloads.Any(d => d.Status == DownloadStatus.Failed);
            if (anyFailed)
            {
                return;
            }

            bool anyActiveOrQueued = Downloads.Any(d =>
                d.Status == DownloadStatus.Downloading ||
                d.Status == DownloadStatus.Assembling ||
                d.Status == DownloadStatus.Queued);

            bool allCompleted = Downloads.All(d => d.Status == DownloadStatus.Completed);

            if (!anyActiveOrQueued && allCompleted)
            {
                _shutdownTriggered = true;
                TriggerShutdown();
            }
        }
    }

    protected virtual void TriggerShutdown()
    {
        if (ShutdownAction != null)
        {
            ShutdownAction.Invoke();
        }
        else
        {
            VortexDownloader.Core.Common.ShutdownManager.ShutdownComputer();
        }
    }

    public ICommand StartAllCommand { get; }
    public ICommand PauseAllCommand { get; }
    public ICommand ClearCompletedCommand { get; }
    public ICommand PauseTaskCommand { get; }
    public ICommand ResumeTaskCommand { get; }
    public ICommand DeleteTaskCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RefreshLinkCommand { get; }
    public ICommand OpenPkgInspectorCommand { get; }

    public MainViewModel(DownloadEngine? engine = null)
    {
        _uiContext = SynchronizationContext.Current;
        _engine = engine ?? new DownloadEngine();

        _engine.TaskAdded += OnEngineTaskAdded;
        _engine.TaskUpdated += OnEngineTaskUpdated;
        _engine.TaskRemoved += OnEngineTaskRemoved;
        Downloads.CollectionChanged += (_, _) => UpdateAggregates();

        StartAllCommand = new RelayCommand(() =>
        {
            _engine.StartAll();
            foreach (var item in Downloads.Where(d => d.Status == DownloadStatus.Queued).ToArray())
                item.StartAction?.Invoke();
        });
        PauseAllCommand = new RelayCommand(async () => await _engine.PauseAllAsync());
        ClearCompletedCommand = new RelayCommand(async () =>
        {
            var completed = Downloads.Where(d => d.Status == DownloadStatus.Completed).ToList();
            foreach (var item in completed)
            {
                await _engine.RemoveTaskAsync(item.Id);
                if (Downloads.Contains(item))
                {
                    Downloads.Remove(item);
                }
            }
        });
        PauseTaskCommand = new RelayCommand<DownloadItemViewModel>(async item =>
        {
            if (item != null)
            {
                await _engine.PauseTaskAsync(item.Id);
                item.CancelAction?.Invoke();
            }
        });
        ResumeTaskCommand = new RelayCommand<DownloadItemViewModel>(async item =>
        {
            if (item != null) await _engine.ResumeTaskAsync(item.Id);
        });
        DeleteTaskCommand = new RelayCommand<DownloadItemViewModel>(async item =>
        {
            if (item != null)
            {
                // Finished downloads leave a real file behind: ask before touching the disk.
                bool deleteFile = false;
                if (item.Status == DownloadStatus.Completed && File.Exists(item.DestinationPath))
                {
                    var answer = Views.ConfirmDeleteDialog.Ask(Application.Current?.MainWindow, item.FileName, item.DestinationPath);
                    if (answer == null) return;
                    deleteFile = answer.Value;
                }

                await _engine.RemoveTaskAsync(item.Id);
                if (deleteFile)
                {
                    try { File.Delete(item.DestinationPath); }
                    catch (Exception ex) { MessageBox.Show($"Dosya silinemedi: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error); }
                }
                item.CancelAction?.Invoke();
                if (Downloads.Contains(item))
                {
                    Downloads.Remove(item);
                }
            }
        });
        OpenFolderCommand = new RelayCommand<DownloadItemViewModel>(item =>
        {
            if (item != null && !string.IsNullOrEmpty(item.DestinationPath))
            {
                OpenInExplorer(item.DestinationPath);
            }
        });

        // Opens the page the download came from; the browser extension then sends a fresh link that resumes this task.
        RefreshLinkCommand = new RelayCommand<DownloadItemViewModel>(item =>
        {
            if (item?.CanRefreshLink != true) return; // CanRefreshLink only allows absolute http/https URLs
            try { Process.Start(new ProcessStartInfo(item.SourcePageUrl!) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show($"Sayfa açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error); }
        });

        // Read-only PS4 PKG viewer for a finished .pkg download.
        OpenPkgInspectorCommand = new RelayCommand<DownloadItemViewModel>(item =>
        {
            if (item?.IsPkgFile != true || !File.Exists(item.DestinationPath)) return;
            new Views.PkgInspectorWindow(item.DestinationPath) { Owner = Application.Current?.MainWindow }.Show();
        });

        SetFilterCommand = new RelayCommand<string>(filter =>
        {
            if (!string.IsNullOrEmpty(filter))
            {
                CurrentFilter = filter;
            }
        });
    }

    public async Task InitializeAsync()
    {
        await _engine.InitializeAsync();
    }

    private void ApplySpeedLimit(string limitOption)
    {
        _userLimitBytes = limitOption switch
        {
            "1 MB/s" => 1024 * 1024,
            "5 MB/s" => 5 * 1024 * 1024,
            "10 MB/s" => 10 * 1024 * 1024,
            _ => 0 // Sınırsız
        };

        ApplyEffectiveLimit();
    }

    private void OnEngineTaskAdded(DownloadTask task)
    {
        PostToUi(() =>
        {
            var vm = new DownloadItemViewModel();
            vm.UpdateFromTask(task);
            Downloads.Insert(0, vm);
            _hasActiveDownloadsSession = true;
            _shutdownTriggered = false;
            UpdateAggregates();
        });
    }

    private void OnEngineTaskUpdated(DownloadTask task)
    {
        PostToUi(() =>
        {
            var existing = Downloads.FirstOrDefault(d => d.Id == task.Id);
            if (existing != null)
            {
                existing.UpdateFromTask(task);
            }
            UpdateAggregates();
        });
    }

    private void OnEngineTaskRemoved(DownloadTask task)
    {
        PostToUi(() =>
        {
            var existing = Downloads.FirstOrDefault(d => d.Id == task.Id);
            if (existing != null)
            {
                Downloads.Remove(existing);
            }
            UpdateAggregates();
        });
    }

    public void UpdateAggregates()
    {
        double sumSpeed = Downloads.Where(d => d.Status == DownloadStatus.Downloading).Sum(d => d.SpeedBytesPerSec);
        TotalSpeedFormatted = DownloadTask.FormatBytesPerSec(sumSpeed);

        // Sparkline: last 24 samples scaled to 2..28 px (floor 1 MB/s so idle noise stays flat)
        if (Environment.TickCount64 - _lastSpeedSampleTick >= 500)
        {
            _lastSpeedSampleTick = Environment.TickCount64;
            _speedSamples.Enqueue(sumSpeed);
            while (_speedSamples.Count > 24) _speedSamples.Dequeue();
            double peak = Math.Max(1024 * 1024, _speedSamples.Max());
            SpeedBars = _speedSamples.Select(s => 2 + 12 * s / peak).ToList();
        }
        ActiveDownloadsCount = Downloads.Count(d => d.Status == DownloadStatus.Downloading);
        if (ActiveDownloadsCount > 0)
        {
            _hasActiveDownloadsSession = true;
        }
        OnPropertyChanged(nameof(TotalDownloadsCount));
        OnPropertyChanged(nameof(HasNoDownloads));
        OnPropertyChanged(nameof(PausedDownloadsCount));
        OnPropertyChanged(nameof(CompletedDownloadsCount));
        OnPropertyChanged(nameof(CompressedCount));
        OnPropertyChanged(nameof(DocumentsCount));
        OnPropertyChanged(nameof(MusicCount));
        OnPropertyChanged(nameof(ProgramsCount));
        OnPropertyChanged(nameof(VideoCount));
        OnPropertyChanged(nameof(FilteredDownloads));
        OnPropertyChanged(nameof(FilteredDownloadsCount));
        OnPropertyChanged(nameof(FreeDiskSpaceText));
        OnPropertyChanged(nameof(TotalActiveProgressRatio));
        OnPropertyChanged(nameof(HasIndeterminateProgress));
        CheckAutoShutdown();
    }

    private void PostToUi(Action action)
    {
        if (_uiContext != null)
        {
            _uiContext.Post(_ => action(), null);
        }
        else if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }

    private static void OpenInExplorer(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            else
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Process.Start("explorer.exe", $"\"{dir}\"");
                }
            }
        }
        catch
        {
            // Ignore explorer launch issues
        }
    }
}
