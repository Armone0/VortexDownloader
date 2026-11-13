using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Media;
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
    public int CompressedCount => Downloads.Count(d => FileCategories.Is(d.FileName, FileCategories.Compressed));
    public int DocumentsCount => Downloads.Count(d => FileCategories.Is(d.FileName, FileCategories.Documents));
    public int MusicCount => Downloads.Count(d => FileCategories.Is(d.FileName, FileCategories.Music));
    public int ProgramsCount => Downloads.Count(d => FileCategories.Is(d.FileName, FileCategories.Programs));
    public int VideoCount => Downloads.Count(d => FileCategories.Is(d.FileName, FileCategories.Video));
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

    public IEnumerable<DownloadItemViewModel> FilteredDownloads => Downloads.Where(MatchesView);

    /// <summary>Current sidebar filter + search; also the list's live CollectionView filter (keeps selection across updates).</summary>
    public bool MatchesView(DownloadItemViewModel d) =>
        MatchesCategory(d) && (string.IsNullOrWhiteSpace(_searchText) ||
            d.FileName.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
            d.Url.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

    private bool MatchesCategory(DownloadItemViewModel d) => _currentFilter switch
    {
        "Downloading" => d.Status == DownloadStatus.Downloading,
        "Paused" => d.Status == DownloadStatus.Paused,
        "Completed" => d.Status == DownloadStatus.Completed,
        FileCategories.Compressed or FileCategories.Documents or FileCategories.Music
            or FileCategories.Programs or FileCategories.Video => FileCategories.Is(d.FileName, _currentFilter),
        _ => true
    };

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
        // Card buttons act on the whole selection when the clicked card is part of a multi-selection.
        PauseTaskCommand = new RelayCommand<DownloadItemViewModel>(async item =>
        {
            if (item == null) return;
            foreach (var target in BulkTargets(item))
            {
                if (target != item && !target.CanPause) continue;
                await _engine.PauseTaskAsync(target.Id);
                target.CancelAction?.Invoke();
            }
        });
        ResumeTaskCommand = new RelayCommand<DownloadItemViewModel>(async item =>
        {
            if (item == null) return;
            foreach (var target in BulkTargets(item))
            {
                if (target == item || target.CanResume) await _engine.ResumeTaskAsync(target.Id);
            }
        });
        DeleteTaskCommand = new RelayCommand<DownloadItemViewModel>(async item =>
        {
            if (item != null) await DeleteItemsAsync(BulkTargets(item), alwaysConfirm: false);
        });
        OpenFolderCommand = new RelayCommand<DownloadItemViewModel>(item =>
        {
            if (item == null) return;
            // One Explorer window per folder, not per file.
            foreach (var group in BulkTargets(item).Where(d => !string.IsNullOrEmpty(d.DestinationPath))
                         .GroupBy(d => Path.GetDirectoryName(d.DestinationPath), StringComparer.OrdinalIgnoreCase).Take(10))
            {
                OpenInExplorer(group.First().DestinationPath);
            }
        });

        ConvertToMp4Command = new RelayCommand<DownloadItemViewModel>(async item =>
        {
            if (item?.CanConvertToMp4 == true) await RunConversionAsync(item, FfmpegPreset.RemuxToMp4);
        });
        ExtractAudioCommand = new RelayCommand<DownloadItemViewModel>(async item =>
        {
            if (item?.CanExtractAudio == true) await RunConversionAsync(item, FfmpegPreset.ExtractAudioM4a);
        });
        CancelConvertCommand = new RelayCommand<DownloadItemViewModel>(item => item?.ConvertCts?.Cancel());
        _engine.AutoFolderRoot = () => AppSettings.Current.AutoFolderByType ? AppSettings.Current.DefaultDownloadDirectory : null;

        // Opens the page the download came from; the browser extension then sends a fresh link that resumes this task.
        RefreshLinkCommand = new RelayCommand<DownloadItemViewModel>(item =>
        {
            if (item?.CanRefreshLink != true) return; // CanRefreshLink only allows absolute http/https URLs
            try { Process.Start(new ProcessStartInfo(item.SourcePageUrl!) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show($"Sayfa açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error); }
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

    // ==================== Multi-select + bulk actions ====================

    /// <summary>Selected cards, kept in sync by the main window's list.</summary>
    public IReadOnlyList<DownloadItemViewModel> SelectedItems { get; set; } = [];

    private IReadOnlyList<DownloadItemViewModel> BulkTargets(DownloadItemViewModel item) =>
        SelectedItems.Count > 1 && SelectedItems.Contains(item) ? SelectedItems.ToList() : [item];

    /// <summary>Removes the items; finished files on disk are only deleted after ConfirmDeleteDialog says so.</summary>
    public async Task DeleteItemsAsync(IReadOnlyList<DownloadItemViewModel> items, bool alwaysConfirm)
    {
        if (items.Count == 0) return;
        var owner = Application.Current?.MainWindow;
        var files = items.Where(d => d.Status == DownloadStatus.Completed && File.Exists(d.DestinationPath)).ToList();

        bool deleteFiles = false;
        if (files.Count > 0)
        {
            var answer = items.Count == 1
                ? Views.ConfirmDeleteDialog.Ask(owner, items[0].FileName, items[0].DestinationPath)
                : Views.ConfirmDeleteDialog.AskMany(owner, items.Count, files.Select(f => f.DestinationPath).ToList());
            if (answer == null) return;
            deleteFiles = answer.Value;
        }
        else if (alwaysConfirm || items.Count > 1)
        {
            string what = items.Count == 1 ? $"\"{items[0].FileName}\"" : $"{items.Count} indirme";
            if (!Views.ConfirmDialog.Ask(owner, "İndirmeler kaldırılsın mı?", $"{what} listeden kaldırılacak.", null, "Kaldır")) return;
        }

        var failed = new List<string>();
        foreach (var item in items)
        {
            item.ConvertCts?.Cancel();
            await _engine.RemoveTaskAsync(item.Id);
            if (deleteFiles && files.Contains(item))
            {
                try { File.Delete(item.DestinationPath); }
                catch (Exception ex) { failed.Add($"{item.FileName}: {ex.Message}"); }
            }
            item.CancelAction?.Invoke();
            Downloads.Remove(item);
        }
        if (failed.Count > 0)
        {
            MessageBox.Show($"Dosya silinemedi:\n{string.Join("\n", failed.Take(5))}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== ffmpeg presets on finished media ====================

    public ICommand ConvertToMp4Command { get; }
    public ICommand ExtractAudioCommand { get; }
    public ICommand CancelConvertCommand { get; }

    private readonly Dictionary<DownloadItemViewModel, Task> _conversions = [];

    private async Task RunConversionAsync(DownloadItemViewModel item, FfmpegPreset preset)
    {
        string source = item.DestinationPath;
        using var cts = new CancellationTokenSource();
        item.ConvertCts = cts;
        item.IsConverting = true;
        item.ConvertStatus = "Hazırlanıyor…";

        var work = Task.Run(async () =>
        {
            var sidecar = new SidecarManager();
            string? ffmpeg = sidecar.GetFfmpegPath();
            if (ffmpeg == null)
            {
                // Same first-run flow as media downloads: fetch the sidecars into %APPDATA%\VortexDownloader\bin.
                PostToUi(() => item.ConvertStatus = "FFmpeg indiriliyor…");
                await sidecar.EnsureSidecarsAsync(ct: cts.Token);
                ffmpeg = sidecar.GetFfmpegPath() ?? throw new InvalidOperationException("FFmpeg hazırlanamadı.");
            }
            PostToUi(() => item.ConvertStatus = "Dönüştürülüyor…");
            return await FfmpegPresets.ConvertAsync(new DefaultProcessRunner(), ffmpeg, preset, source,
                p => PostToUi(() => { if (item.IsConverting) item.ConvertStatus = $"Dönüştürülüyor… %{p:0}"; }), cts.Token);
        });
        _conversions[item] = work;

        try
        {
            string output = await work;
            item.ConvertStatus = $"✓ Kaydedildi: {Path.GetFileName(output)}";
        }
        catch (OperationCanceledException)
        {
            item.ConvertStatus = "Dönüştürme iptal edildi";
        }
        catch (Exception ex)
        {
            item.ConvertStatus = $"✗ {ex.Message}";
        }
        finally
        {
            _conversions.Remove(item);
            item.ConvertCts = null;
            item.IsConverting = false;
        }
    }

    /// <summary>App exit: cancel running ffmpeg jobs and wait briefly so the processes are killed, not orphaned.</summary>
    public void CancelConversions()
    {
        if (_conversions.Count == 0) return;
        foreach (var item in _conversions.Keys) item.ConvertCts?.Cancel();
        try { Task.WaitAll(_conversions.Values.ToArray(), TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
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
