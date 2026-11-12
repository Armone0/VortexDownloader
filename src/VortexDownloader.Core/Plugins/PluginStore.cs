using System.Text;
using System.Text.Json;

namespace VortexDownloader.Core.Plugins;

/// <summary>Plugins on disk: &lt;root&gt;\&lt;id&gt;\plugin.json. Manifests are data-only; no downloaded code is ever loaded.</summary>
public sealed class PluginStore(string root)
{
    private const string FileName = "plugin.json";
    private const string SeedMarker = ".seeded";

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VortexDownloader", "plugins");

    public static readonly PluginManifest YouTube = new(
        "media.youtube", "YouTube", "1.0.0",
        "YouTube video ve ses bağlantılarını Vortex indirme kuyruğuna ekler.",
        PluginManifest.MediaProviderType, ["youtube.com", "youtu.be"]);

    /// <summary>First run only: installs the bundled YouTube manifest so it is a normal, removable plugin.</summary>
    public void SeedDefaults()
    {
        Directory.CreateDirectory(root);
        string marker = Path.Combine(root, SeedMarker);
        if (File.Exists(marker)) return;
        Install(YouTube);
        File.WriteAllText(marker, "");
    }

    public IReadOnlyList<IVortexPlugin> LoadAll()
    {
        var list = new List<IVortexPlugin>();
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                var m = PluginManifest.Parse(File.ReadAllText(Path.Combine(dir, FileName)));
                if (!string.Equals(m.Id, Path.GetFileName(dir), StringComparison.Ordinal)) continue; // folder must match id
                list.Add(new ManifestMediaProviderPlugin(m));
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException) { /* skip broken plugin */ }
        }
        return list;
    }

    /// <summary>Downloads a manifest over HTTPS and validates it. Nothing is written until <see cref="Install"/>.</summary>
    public static async Task<PluginManifest> FetchAsync(HttpClient http, Uri url, CancellationToken ct = default)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new FormatException("Eklenti yalnızca https bağlantısından eklenebilir.");
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[PluginManifest.MaxBytes + 1];
        int n = 0, r;
        while (n < buf.Length && (r = await stream.ReadAsync(buf.AsMemory(n), ct).ConfigureAwait(false)) > 0) n += r;
        return PluginManifest.Parse(Encoding.UTF8.GetString(buf, 0, n));
    }

    public void Install(PluginManifest manifest)
    {
        string dir = Path.Combine(root, manifest.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, FileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Remove(string id)
    {
        string dir = Path.Combine(root, id);
        if (Path.GetFileName(dir) == id && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
