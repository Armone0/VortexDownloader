using System.Text.Json;
using System.Text.RegularExpressions;

namespace VortexDownloader.Core.Plugins;

/// <summary>
/// Data-only plugin description (plugin.json). A plugin carries no code: it only declares which
/// sites the built-in yt-dlp media workflow should accept, so installing one can never run downloaded code.
/// </summary>
public sealed record PluginManifest(string Id, string Name, string Version, string Description, string Type, string[] Hosts)
{
    public const int MaxBytes = 64 * 1024;
    public const string MediaProviderType = "media-provider";

    private static readonly Regex IdRx = new(@"^[a-z0-9]+(\.[a-z0-9-]+)+$", RegexOptions.Compiled);
    private static readonly Regex HostRx = new(@"^[a-z0-9-]+(\.[a-z0-9-]+)*\.[a-z]{2,}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parses and validates a manifest; throws <see cref="FormatException"/> with a user-readable reason.</summary>
    public static PluginManifest Parse(string json)
    {
        if (json.Length > MaxBytes) throw new FormatException("Eklenti dosyası çok büyük.");
        PluginManifest? m;
        try { m = JsonSerializer.Deserialize<PluginManifest>(json, Json); }
        catch (JsonException) { throw new FormatException("Eklenti dosyası geçerli JSON değil."); }
        if (m is null) throw new FormatException("Eklenti dosyası boş.");
        if (m.Id is null || !IdRx.IsMatch(m.Id)) throw new FormatException("Geçersiz eklenti kimliği (örn. media.vimeo).");
        if (string.IsNullOrWhiteSpace(m.Name) || m.Name.Length > 60) throw new FormatException("Eklenti adı eksik veya çok uzun.");
        if (!System.Version.TryParse(m.Version, out _)) throw new FormatException("Geçersiz sürüm numarası.");
        if (m.Description is null || m.Description.Length > 300) throw new FormatException("Açıklama eksik veya çok uzun.");
        if (m.Type != MediaProviderType) throw new FormatException($"Desteklenmeyen eklenti türü: {m.Type}");
        if (m.Hosts is null || m.Hosts.Length is 0 or > 20 || m.Hosts.Any(h => h is null || !HostRx.IsMatch(h)))
            throw new FormatException("Site listesi geçersiz (1-20 alan adı, küçük harf).");
        return m;
    }
}
