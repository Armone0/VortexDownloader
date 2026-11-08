namespace VortexDownloader.Core.Media;

public class MediaDownloadOptions
{
    public string Url { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string SelectedFormatId { get; set; } = "bestvideo+bestaudio/best";
    public bool ExtractAudioOnly { get; set; }
    public string AudioFormat { get; set; } = "mp3";
    public int ConcurrentFragments { get; set; } = 8;
    public bool DownloadSubtitles { get; set; }
}
