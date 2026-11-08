namespace VortexDownloader.Core.Media;

public class MediaFormatInfo
{
    public string FormatId { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string? VCodec { get; set; }
    public string? ACodec { get; set; }
    public long? FilesizeApprox { get; set; }
    public bool IsVideoOnly { get; set; }
    public bool IsAudioOnly { get; set; }
    public string? Note { get; set; }
}
