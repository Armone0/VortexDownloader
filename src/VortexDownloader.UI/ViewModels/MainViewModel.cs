using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using VortexDownloader.Core.Engine;
using VortexDownloader.Core.Models;

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

    public string TotalSpeedFormatted
    {
        get => _totalSpeedFormatted;
        set => SetProperty(ref _totalSpeedFormatted, value);
    }

    public int ActiveDownloadsCount
    {
        get => _activeDownloadsCount;
        set => SetProperty(ref _activeDownloadsCount, value);
    }

    public int TotalDownloadsCount => Downloads.Count;
    public bool HasNoDownloads => Downloads.Count == 0;
    public int PausedDownloadsCount => Downloads.Count(d => d.Status == DownloadStatus.Paused);
    public int CompletedDownloadsCount => Downloads.Count(d => d.Status == DownloadStatus.Completed);

    private string _currentFilter = "All";
    public string CurrentFilter
    {
        get => _currentFilter;
        set
        {
            if (SetProperty(ref _currentFilter, value))
            {
                OnPropertyChanged(nameof(FilterTitle));
                OnPropertyChanged(nameof(FilteredDownloads));
                OnPropertyChanged(nameof(IsAllFilter));
                OnPropertyChanged(nameof(IsDownloadingFilter));
                OnPropertyChanged(nameof(IsPausedFilter));
                OnPropertyChanged(nameof(IsCompletedFilter));
            }
        }
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
        "Grabber" => "Yakalayıcı Projeleri",
        "Queues" => "İndirme Kuyrukları",
        _ => "Tüm İndirmeler"
    };

    public IEnumerable<DownloadItemViewModel> FilteredDownloads => _currentFilter switch
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
            bool anyActiveOrQueued = Downloads.Any(d =>
                d.Status == DownloadStatus.Downloading ||
                d.Status == DownloadStatus.Assembling ||
                d.Status == DownloadStatus.Queued);

            if (!anyActiveOrQueued)
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

    public MainViewModel(DownloadEngine? engine = null)
    {
        _uiContext = SynchronizationContext.Current;
        _engine = engine ?? new DownloadEngine();

        _engine.TaskAdded += OnEngineTaskAdded;
        _engine.TaskUpdated += OnEngineTaskUpdated;
        _engine.TaskRemoved += OnEngineTaskRemoved;

        StartAllCommand = new RelayCommand(() => _engine.StartAll());
        PauseAllCommand = new RelayCommand(async () => await _engine.PauseAllAsync());
        ClearCompletedCommand = new RelayCommand(async () =>
        {
            var completed = Downloads.Where(d => d.Status == DownloadStatus.Completed).ToList();
            foreach (var item in completed)
            {
                await _engine.RemoveTaskAsync(item.Id);
            }
        });
        PauseTaskCommand = new RelayCommand<DownloadItemViewModel>(async item =>
        {
            if (item != null) await _engine.PauseTaskAsync(item.Id);
        });
        ResumeTaskCommand = new RelayCommand<DownloadItemViewModel>(async item =>
        {
            if (item != null) await _engine.ResumeTaskAsync(item.Id);
        });
        DeleteTaskCommand = new RelayCommand<DownloadItemViewModel>(async item =>
        {
            if (item != null) await _engine.RemoveTaskAsync(item.Id);
        });
        OpenFolderCommand = new RelayCommand<DownloadItemViewModel>(item =>
        {
            if (item != null && !string.IsNullOrEmpty(item.DestinationPath))
            {
                OpenInExplorer(item.DestinationPath);
            }
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
        long bytesPerSec = limitOption switch
        {
            "1 MB/s" => 1024 * 1024,
            "5 MB/s" => 5 * 1024 * 1024,
            "10 MB/s" => 10 * 1024 * 1024,
            _ => 0 // Sınırsız
        };

        _engine.SetGlobalSpeedLimit(bytesPerSec);
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

    private void UpdateAggregates()
    {
        double sumSpeed = Downloads.Where(d => d.Status == DownloadStatus.Downloading).Sum(d => d.SpeedBytesPerSec);
        TotalSpeedFormatted = DownloadTask.FormatBytesPerSec(sumSpeed);
        ActiveDownloadsCount = Downloads.Count(d => d.Status == DownloadStatus.Downloading);
        if (ActiveDownloadsCount > 0)
        {
            _hasActiveDownloadsSession = true;
        }
        OnPropertyChanged(nameof(TotalDownloadsCount));
        OnPropertyChanged(nameof(HasNoDownloads));
        OnPropertyChanged(nameof(PausedDownloadsCount));
        OnPropertyChanged(nameof(CompletedDownloadsCount));
        OnPropertyChanged(nameof(FilteredDownloads));
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
