namespace VortexDownloader.Core.Models;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Assembling,
    Completed,
    Failed,
    Canceled
}
