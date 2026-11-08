using System.Text.Json;

namespace VortexDownloader.Core.Media;

public class MediaMetadata
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string WebpageUrl { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public string Uploader { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<MediaFormatInfo> AvailableFormats { get; set; } = [];

    public static MediaMetadata FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var metadata = new MediaMetadata
        {
            Id = GetString(root, "id"),
            Title = GetString(root, "title"),
            WebpageUrl = GetString(root, "webpage_url", "original_url", "url"),
            ThumbnailUrl = GetString(root, "thumbnail"),
            DurationSeconds = GetDouble(root, "duration"),
            Uploader = GetString(root, "uploader", "channel", "uploader_id"),
            Description = GetString(root, "description")
        };

        if (root.TryGetProperty("formats", out var formatsElement) && formatsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formatsElement.EnumerateArray())
            {
                var vcodec = GetNullableString(f, "vcodec");
                var acodec = GetNullableString(f, "acodec");

                var isVideoOnly = !string.IsNullOrEmpty(vcodec) && vcodec != "none" && (string.IsNullOrEmpty(acodec) || acodec == "none");
                var isAudioOnly = (string.IsNullOrEmpty(vcodec) || vcodec == "none") && !string.IsNullOrEmpty(acodec) && acodec != "none";

                var height = GetInt(f, "height");
                var resolution = GetString(f, "resolution");
                if (height > 0)
                {
                    resolution = $"{height}p";
                }
                else if (string.IsNullOrEmpty(resolution) || resolution.Equals("multiple", StringComparison.OrdinalIgnoreCase))
                {
                    resolution = isAudioOnly ? "audio only" : "";
                }

                long? filesize = null;
                if (f.TryGetProperty("filesize", out var fsProp) && fsProp.ValueKind == JsonValueKind.Number)
                {
                    filesize = fsProp.GetInt64();
                }
                else if (f.TryGetProperty("filesize_approx", out var fsaProp) && fsaProp.ValueKind == JsonValueKind.Number)
                {
                    filesize = fsaProp.GetInt64();
                }

                metadata.AvailableFormats.Add(new MediaFormatInfo
                {
                    FormatId = GetString(f, "format_id"),
                    Resolution = resolution,
                    Extension = GetString(f, "ext"),
                    VCodec = vcodec,
                    ACodec = acodec,
                    FilesizeApprox = filesize,
                    IsVideoOnly = isVideoOnly,
                    IsAudioOnly = isAudioOnly,
                    Note = GetNullableString(f, "format_note")
                });
            }
        }

        return metadata;
    }

    private static string GetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var prop in propertyNames)
        {
            if (element.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
            {
                return val.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static string? GetNullableString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var val) && val.ValueKind == JsonValueKind.String)
        {
            return val.GetString();
        }
        return null;
    }

    private static double GetDouble(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var val) && val.ValueKind == JsonValueKind.Number)
        {
            return val.GetDouble();
        }
        return 0.0;
    }

    private static int GetInt(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var val) && val.ValueKind == JsonValueKind.Number)
        {
            return val.GetInt32();
        }
        return 0;
    }
}
