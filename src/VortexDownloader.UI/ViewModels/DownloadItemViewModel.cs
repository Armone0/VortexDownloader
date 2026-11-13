using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using VortexDownloader.Core.Common;
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

    private string? _customStatusText;

    public string? CustomStatusText
    {
        get => _customStatusText;
        set
        {
            if (SetProperty(ref _customStatusText, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public Action? CancelAction { get; set; }
    public Action? StartAction { get; set; }

    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetProperty(ref _totalBytes, value))
            {
                OnPropertyChanged(nameof(TotalBytesFormatted));
            }
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (SetProperty(ref _downloadedBytes, value))
            {
                OnPropertyChanged(nameof(DownloadedBytesFormatted));
            }
        }
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
                OnPropertyChanged(nameof(IsDone));
            }
        }
    }

    public string StatusText => !string.IsNullOrEmpty(_customStatusText)
        ? _customStatusText
        : Status switch
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

    private IReadOnlyList<double> _chunkFractions = [];

    public IReadOnlyList<double> ChunkFractions
    {
        get => _chunkFractions;
        private set => SetProperty(ref _chunkFractions, value);
    }

    internal static IReadOnlyList<double> BuildChunkSlots(DownloadTask task)
    {
        long total = task.TotalBytes;
        if (!task.SupportsRange || total <= 0 || task.Chunks.Count <= 1) return [];

        int n = Math.Clamp(task.MaxChunks, 2, 16);
        var filled = new double[n];
        foreach (var c in task.Chunks)
        {
            // Each chunk is downloaded front-to-back, so its done bytes are [StartPos, StartPos + done)
            long done = c.IsCompleted ? c.EndPos - c.StartPos + 1 : Math.Clamp(c.DownloadedBytes, 0, Math.Max(0, c.TotalBytes));
            long a = c.StartPos, b = Math.Min(total, a + done);
            if (b <= a) continue;
            int first = (int)Math.Min(n - 1, a * n / total), last = (int)Math.Min(n - 1, (b - 1) * n / total);
            for (int i = first; i <= last; i++)
            {
                long lo = i * total / n, hi = (i + 1) * total / n;
                filled[i] += Math.Max(0, Math.Min(b, hi) - Math.Max(a, lo));
            }
        }
        var slots = new double[n];
        for (int i = 0; i < n; i++)
        {
            long size = (i + 1) * total / n - i * total / n;
            slots[i] = size > 0 ? Math.Clamp(filled[i] / size, 0, 1) : 1;
        }
        return slots;
    }

    public bool IsDone => Status == DownloadStatus.Completed;
    public bool HasChunkMap => _chunkFractions.Count > 1;
    public bool HasNoChunkMap => !HasChunkMap;
    public bool HasError => Status == DownloadStatus.Failed && !string.IsNullOrWhiteSpace(ErrorMessage);

    public string? SourcePageUrl { get; private set; }
    public bool LinkExpired { get; private set; }
    public bool CanRefreshLink => LinkExpired && Status == DownloadStatus.Failed
        && Uri.TryCreate(SourcePageUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    // Multi-mirror chip: only shown when the task has mirrors.
    private string _mirrorTooltip = string.Empty;
    public bool HasMirrors => _mirrorTooltip.Length > 0;
    public string MirrorChipText { get; private set; } = string.Empty;
    public string MirrorTooltip
    {
        get => _mirrorTooltip;
        private set
        {
            if (SetProperty(ref _mirrorTooltip, value))
            {
                OnPropertyChanged(nameof(HasMirrors));
                OnPropertyChanged(nameof(MirrorChipText));
            }
        }
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    public bool CanPause => SupportsPause && Status == DownloadStatus.Downloading;

    /// <summary>False for cards the engine doesn't own (e.g. files fetched out of a ZIP), so no dead Pause button shows.</summary>
    public bool SupportsPause { get; init; } = true;
    public bool CanResume => Status == DownloadStatus.Paused || Status == DownloadStatus.Failed || Status == DownloadStatus.Canceled;
    public bool IsActive => Status == DownloadStatus.Downloading || Status == DownloadStatus.Assembling;

    public string ExtensionLabel
    {
        get
        {
            string ext = System.IO.Path.GetExtension(FileName).TrimStart('.').ToUpperInvariant();
            return ext.Length is > 0 and <= 4 ? ext : "FILE";
        }
    }

    public string EtaFormatted
    {
        get
        {
            if (Status != DownloadStatus.Downloading || SpeedBytesPerSec < 1 || TotalBytes <= 0) return string.Empty;
            var t = TimeSpan.FromSeconds((TotalBytes - DownloadedBytes) / SpeedBytesPerSec);
            return $"Kalan {(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    public string DownloadedBytesFormatted => DownloadTask.FormatBytes(DownloadedBytes);
    public string TotalBytesFormatted => TotalBytes > 0 ? DownloadTask.FormatBytes(TotalBytes) : "Bilinmiyor";
    public string SpeedFormatted => FormattedSpeed;
    public int ChunksCount => _chunksCount;

    // Chunks multiply as idle connections split the largest remaining piece, so the badge shows connections (the user's choice).
    public int ConnectionCount => _connectionCount;
    public string ConnectionTooltip => _chunksCount > _connectionCount
        ? $"{_connectionCount} eşzamanlı bağlantı · dosya {_chunksCount} parçada indirildi (boşa çıkan bağlantılar kalan en büyük parçayı böldü)"
        : $"{_connectionCount} eşzamanlı bağlantı";

    private int _chunksCount = 1;
    private int _connectionCount = 1;

    public string FormattedSpeed
    {
        get => _formattedSpeed;
        set
        {
            if (SetProperty(ref _formattedSpeed, value))
            {
                OnPropertyChanged(nameof(SpeedFormatted));
            }
        }
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

    // --- Post-download integrity (SHA-256 + Defender) ---
    private string? _sha256;
    private ScanStatus _scanStatus;
    private string _expectedHash = string.Empty;

    public string? Sha256
    {
        get => _sha256;
        private set
        {
            if (SetProperty(ref _sha256, value))
            {
                OnPropertyChanged(nameof(HasSha256));
                OnPropertyChanged(nameof(HashTooltip));
                OnPropertyChanged(nameof(IsHashMatch));
                OnPropertyChanged(nameof(IsHashMismatch));
            }
        }
    }

    public ScanStatus ScanStatus
    {
        get => _scanStatus;
        private set
        {
            if (SetProperty(ref _scanStatus, value))
            {
                OnPropertyChanged(nameof(ScanBadgeText));
                OnPropertyChanged(nameof(HasScanBadge));
            }
        }
    }

    public string ExpectedHash
    {
        get => _expectedHash;
        set
        {
            if (SetProperty(ref _expectedHash, value))
            {
                OnPropertyChanged(nameof(IsHashMatch));
                OnPropertyChanged(nameof(IsHashMismatch));
            }
        }
    }

    public bool HasSha256 => !string.IsNullOrEmpty(_sha256);
    public string HashTooltip => $"SHA-256: {_sha256}";
    public bool IsHashMatch => FileHasher.Matches(_sha256, _expectedHash);
    public bool IsHashMismatch => HasSha256 && !string.IsNullOrWhiteSpace(_expectedHash) && !IsHashMatch;

    public string ScanBadgeText => _scanStatus switch
    {
        ScanStatus.Scanning => "Taranıyor…",
        ScanStatus.Clean => "✓ Temiz",
        ScanStatus.Threat => "⚠ Tehdit bulundu",
        _ => string.Empty
    };
    public bool HasScanBadge => ScanBadgeText.Length > 0;

    private System.Windows.Input.ICommand? _copyHashCommand;
    public System.Windows.Input.ICommand CopyHashCommand => _copyHashCommand ??= new RelayCommand(() =>
    {
        if (!HasSha256) return;
        try { System.Windows.Clipboard.SetText(_sha256!); } catch { /* clipboard busy */ }
    });

    public void UpdateFromTask(DownloadTask task)
    {
        Id = task.Id;
        Sha256 = task.Sha256;
        ScanStatus = task.ScanStatus;
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
        SourcePageUrl = task.SourcePageUrl;
        LinkExpired = task.LinkExpired;
        OnPropertyChanged(nameof(CanRefreshLink));

        if (task.MirrorUrls.Count > 0)
        {
            var dead = task.DeadSources;
            MirrorChipText = $"⇄ {task.MirrorUrls.Count + 1} kaynak";
            MirrorTooltip = string.Join("\n", new[] { task.Url }.Concat(task.MirrorUrls)
                .Select(s => dead.Contains(s) ? $"✕ {HostOf(s)} (devre dışı)" : $"• {HostOf(s)}"));
        }

        // Chunk map: fixed byte-range slots (one per connection, max 16). Chunks keep splitting during a download,
        // so drawing one bar per chunk turned into 40+ dots; slots stay stable and show where bytes are missing.
        lock (task.ChunksLock)
        {
            ChunkFractions = BuildChunkSlots(task);
        }
        OnPropertyChanged(nameof(ExtensionLabel));
        OnPropertyChanged(nameof(EtaFormatted));
        OnPropertyChanged(nameof(HasChunkMap));
        OnPropertyChanged(nameof(HasNoChunkMap));
        OnPropertyChanged(nameof(HasError));

        int totalChunks = task.Chunks.Count;
        int activeChunks = task.ActiveChunkCount;
        _chunksCount = totalChunks > 0 ? totalChunks : 1;
        _connectionCount = task.SupportsRange ? Math.Max(1, task.MaxChunks) : 1;
        ChunkInfo = task.SupportsRange
            ? (activeChunks > 0 ? $"{activeChunks}/{totalChunks} Parça Dinamik" : $"{totalChunks} Parça Dinamik")
            : "Tek Parça (Range Yok)";

        OnPropertyChanged(nameof(DownloadedBytesFormatted));
        OnPropertyChanged(nameof(TotalBytesFormatted));
        OnPropertyChanged(nameof(SpeedFormatted));
        OnPropertyChanged(nameof(ChunksCount));
        OnPropertyChanged(nameof(ConnectionCount));
        OnPropertyChanged(nameof(ConnectionTooltip));
    }
}
