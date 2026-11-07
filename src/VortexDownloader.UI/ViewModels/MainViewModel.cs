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

    public ICommand StartAllCommand { get; }
    public ICommand PauseAllCommand { get; }
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
        OnPropertyChanged(nameof(TotalDownloadsCount));
        OnPropertyChanged(nameof(HasNoDownloads));
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
