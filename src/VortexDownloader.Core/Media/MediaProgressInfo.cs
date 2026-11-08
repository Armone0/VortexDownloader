namespace VortexDownloader.Core.Media;

public enum MediaDownloadStatus
{
    Inspecting,
    Downloading,
    Remuxing,
    Completed,
    Error
}

public class MediaProgressInfo
{
    public double Percentage { get; set; }
    public string SpeedText { get; set; } = string.Empty;
    public string EtaText { get; set; } = string.Empty;
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public MediaDownloadStatus Status { get; set; } = MediaDownloadStatus.Downloading;
}
