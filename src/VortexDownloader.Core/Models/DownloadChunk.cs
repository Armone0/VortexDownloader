using System.Text.Json.Serialization;

namespace VortexDownloader.Core.Models;

public class DownloadChunk
{
    public int Id { get; set; }
    public Guid TaskId { get; set; }
    public long StartPos { get; set; }
    public long CurrentPos { get; set; }
    public long EndPos { get; set; }
    public string TempFilePath { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }

    [JsonIgnore]
    public bool IsActive { get; set; }

    [JsonIgnore]
    public long DownloadedBytes => Math.Max(0, CurrentPos - StartPos);

    [JsonIgnore]
    public long TotalBytes => EndPos >= StartPos ? (EndPos - StartPos + 1) : -1;

    [JsonIgnore]
    public long RemainingBytes
    {
        get
        {
            if (IsCompleted) return 0;
            return EndPos >= CurrentPos ? (EndPos - CurrentPos + 1) : 0;
        }
    }

    [JsonIgnore]
    public double ProgressPercentage
    {
        get
        {
            if (IsCompleted) return 100.0;
            long total = TotalBytes;
            if (total <= 0) return 0.0;
            return Math.Clamp((double)DownloadedBytes / total * 100.0, 0.0, 100.0);
        }
    }
}
