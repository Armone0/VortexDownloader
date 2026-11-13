using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace VortexDownloader.Core.Sources;

public sealed record GitHubAsset(string Name, long Size, int DownloadCount, string Url, string? Sha256);

public sealed record GitHubRelease(string Name, string Tag, DateTimeOffset? PublishedAt, bool Prerelease, string Body,
    IReadOnlyList<GitHubAsset> Assets);

/// <summary>GitHub answered 403/429 with no quota left; <see cref="ResetAt"/> is when it refills.</summary>
public sealed class GitHubRateLimitException(DateTimeOffset resetAt)
    : Exception($"GitHub limiti doldu, {resetAt.ToLocalTime():HH:mm}'de tekrar dene")
{
    public DateTimeOffset ResetAt { get; } = resetAt;
}

public sealed class GitHubRepoNotFoundException() : Exception("Depo bulunamadı veya özel");

/// <summary>Lists releases of a public GitHub repo (unauthenticated API, 60 requests/hour).</summary>
public sealed class GitHubReleaseClient(HttpClient http)
{
    public const int MaxResponseBytes = 4 * 1024 * 1024;
    public const int MaxBodyChars = 4000;

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    public GitHubReleaseClient() : this(SharedHttp) { }

    public static bool TryParseRepo(string? url, out string owner, out string repo, out string? tag)
    {
        owner = repo = string.Empty; tag = null;
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        var seg = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (seg.Length < 2) return false;
        string r = seg[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? seg[1][..^4] : seg[1];
        if (!ValidName(seg[0]) || !ValidName(r)) return false;

        if (seg.Length == 2) { }
        else if (seg[2] == "releases")
        {
            if (seg.Length == 4 && seg[3] == "latest") { }
            else if (seg.Length >= 5 && seg[3] == "tag") tag = string.Join('/', seg.Skip(4));
            else if (seg.Length != 3) return false;
        }
        else return false;

        owner = seg[0]; repo = r;
        return true;
    }

    private static bool ValidName(string s) =>
        s.Length is > 0 and <= 100 && s != "." && s != ".." && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static bool IsAllowedAssetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(string owner, string repo, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases?per_page=30");
        string ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1";
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("VortexDownloader", ver));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) throw new GitHubRepoNotFoundException();
        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            if (resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rem) && rem.FirstOrDefault() == "0" &&
                resp.Headers.TryGetValues("X-RateLimit-Reset", out var rst) && long.TryParse(rst.FirstOrDefault(), out long unix))
                throw new GitHubRateLimitException(DateTimeOffset.FromUnixTimeSeconds(unix));
        }
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("GitHub yanıtı çok büyük");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxResponseBytes) throw new InvalidDataException("GitHub yanıtı çok büyük");
            ms.Write(buf, 0, n);
        }
        return ParseReleases(ms.ToArray());
    }

    public static IReadOnlyList<GitHubRelease> ParseReleases(ReadOnlySpan<byte> json)
    {
        var list = new List<GitHubRelease>();
        var reader = new Utf8JsonReader(json);
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (Bool(r, "draft")) continue;
            string tag = Str(r, "tag_name") ?? "";
            string body = Str(r, "body") ?? "";
            if (body.Length > MaxBodyChars) body = body[..MaxBodyChars] + "…";

            var assets = new List<GitHubAsset>();
            if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray())
                {
                    string? url = Str(a, "browser_download_url");
                    if (!IsAllowedAssetUrl(url)) continue;
                    string? digest = Str(a, "digest");
                    string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest[7..].ToLowerInvariant() : null;
                    assets.Add(new GitHubAsset(Str(a, "name") ?? "", a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long s) ? s : 0,
                        a.TryGetProperty("download_count", out var dc) && dc.TryGetInt32(out int d) ? d : 0, url!, sha));
                }

            DateTimeOffset? pub = DateTimeOffset.TryParse(Str(r, "published_at"), out var p) ? p : null;
            string name = string.IsNullOrWhiteSpace(Str(r, "name")) ? tag : Str(r, "name")!;
            list.Add(new GitHubRelease(name, tag, pub, Bool(r, "prerelease"), body, assets));
        }
        return list;
    }

    private static string? Str(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
}
