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

/// <summary>Post-download antivirus result. Unavailable = no Defender / unknown result (not an error).</summary>
public enum ScanStatus
{
    None,
    Scanning,
    Clean,
    Threat,
    Unavailable
}
