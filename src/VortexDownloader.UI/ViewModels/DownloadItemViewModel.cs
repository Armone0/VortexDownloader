using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using VortexDownloader.Core.Models;

namespace VortexDownloader.UI.ViewModels;

public class DownloadItemViewModel : ViewModelBase
{
    private Guid _id;
    private string _fileName = string.Empty;
    private string _url = string.Empty;
    private string _destinationPath = string.Empty;
    private long _totalBytes;
    private long _downloadedBytes;
    private double _speedBytesPerSec;
    private DownloadStatus _status;
    private string _formattedSpeed = "0 KB/s";
    private string _formattedProgress = "0 B / 0 B (0%)";
    private double _progressPercentage;
    private string _chunkInfo = "1 Parça";
    private string? _errorMessage;

    public Guid Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    public string Url
    {
        get => _url;
        set => SetProperty(ref _url, value);
    }

    public string DestinationPath
    {
        get => _destinationPath;
        set => SetProperty(ref _destinationPath, value);
    }

    public long TotalBytes
    {
        get => _totalBytes;
        set => SetProperty(ref _totalBytes, value);
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set => SetProperty(ref _downloadedBytes, value);
    }

    public double SpeedBytesPerSec
    {
        get => _speedBytesPerSec;
        set => SetProperty(ref _speedBytesPerSec, value);
    }

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(StatusBgColor));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(IsActive));
            }
        }
    }

    public string StatusText => Status switch
    {
        DownloadStatus.Queued => "Sırada",
        DownloadStatus.Downloading => "İndiriliyor",
        DownloadStatus.Paused => "Duraklatıldı",
        DownloadStatus.Assembling => "Birleştiriliyor",
        DownloadStatus.Completed => "Tamamlandı",
        DownloadStatus.Failed => "Hata",
        DownloadStatus.Canceled => "İptal Edildi",
        _ => Status.ToString()
    };

    private static readonly SolidColorBrush EmeraldBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(52, 211, 153)));
    private static readonly SolidColorBrush AmberBrush   = FreezeBrush(new SolidColorBrush(Color.FromRgb(251, 191, 36)));
    private static readonly SolidColorBrush PurpleBrush  = FreezeBrush(new SolidColorBrush(Color.FromRgb(192, 132, 252)));
    private static readonly SolidColorBrush BlueBrush    = FreezeBrush(new SolidColorBrush(Color.FromRgb(96, 165, 250)));
    private static readonly SolidColorBrush RoseBrush    = FreezeBrush(new SolidColorBrush(Color.FromRgb(248, 113, 113)));
    private static readonly SolidColorBrush SlateBrush   = FreezeBrush(new SolidColorBrush(Color.FromRgb(148, 163, 184)));

    private static readonly SolidColorBrush EmeraldBgBrush = FreezeBrush(new SolidColorBrush(Color.FromArgb(40, 52, 211, 153)));
    private static readonly SolidColorBrush AmberBgBrush   = FreezeBrush(new SolidColorBrush(Color.FromArgb(40, 251, 191, 36)));
    private static readonly SolidColorBrush PurpleBgBrush  = FreezeBrush(new SolidColorBrush(Color.FromArgb(40, 192, 132, 252)));
    private static readonly SolidColorBrush BlueBgBrush    = FreezeBrush(new SolidColorBrush(Color.FromArgb(40, 96, 165, 250)));
    private static readonly SolidColorBrush RoseBgBrush    = FreezeBrush(new SolidColorBrush(Color.FromArgb(40, 248, 113, 113)));
    private static readonly SolidColorBrush SlateBgBrush   = FreezeBrush(new SolidColorBrush(Color.FromArgb(40, 148, 163, 184)));

    private static SolidColorBrush FreezeBrush(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    public Brush StatusColor => Status switch
    {
        DownloadStatus.Downloading => EmeraldBrush,
        DownloadStatus.Paused => AmberBrush,
        DownloadStatus.Assembling => PurpleBrush,
        DownloadStatus.Completed => BlueBrush,
        DownloadStatus.Failed => RoseBrush,
        _ => SlateBrush
    };

    public Brush StatusBgColor => Status switch
    {
        DownloadStatus.Downloading => EmeraldBgBrush,
        DownloadStatus.Paused => AmberBgBrush,
        DownloadStatus.Assembling => PurpleBgBrush,
        DownloadStatus.Completed => BlueBgBrush,
        DownloadStatus.Failed => RoseBgBrush,
        _ => SlateBgBrush
    };

    public bool CanPause => Status == DownloadStatus.Downloading;
    public bool CanResume => Status == DownloadStatus.Paused || Status == DownloadStatus.Failed || Status == DownloadStatus.Canceled;
    public bool IsActive => Status == DownloadStatus.Downloading || Status == DownloadStatus.Assembling;

    public string FormattedSpeed
    {
        get => _formattedSpeed;
        set => SetProperty(ref _formattedSpeed, value);
    }

    public string FormattedProgress
    {
        get => _formattedProgress;
        set => SetProperty(ref _formattedProgress, value);
    }

    public double ProgressPercentage
    {
        get => _progressPercentage;
        set => SetProperty(ref _progressPercentage, value);
    }

    public string ChunkInfo
    {
        get => _chunkInfo;
        set => SetProperty(ref _chunkInfo, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public void UpdateFromTask(DownloadTask task)
    {
        Id = task.Id;
        FileName = task.FileName;
        Url = task.Url;
        DestinationPath = task.DestinationPath;
        TotalBytes = task.TotalBytes;
        DownloadedBytes = task.DownloadedBytes;
        SpeedBytesPerSec = task.SpeedBytesPerSec;
        Status = task.Status;
        ProgressPercentage = task.ProgressPercentage;
        FormattedSpeed = task.FormattedSpeed;
        FormattedProgress = task.FormattedProgress;
        ErrorMessage = task.ErrorMessage;

        int totalChunks = task.Chunks.Count;
        int activeChunks = task.ActiveChunkCount;
        ChunkInfo = task.SupportsRange
            ? (activeChunks > 0 ? $"{activeChunks}/{totalChunks} Parça Dinamik" : $"{totalChunks} Parça Dinamik")
            : "Tek Parça (Range Yok)";
    }
}
