using System.IO.Compression;

namespace VortexDownloader.Core.Media;

public class SidecarManager
{
    public const string DefaultYtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string DefaultFfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    public string AppDataBinDir { get; set; }
    public string AppBaseBinDir { get; set; }
    public string YtDlpDownloadUrl { get; set; } = DefaultYtDlpUrl;
    public string FfmpegDownloadUrl { get; set; } = DefaultFfmpegUrl;
    public string? CustomPathEnvironment { get; set; }

    private readonly HttpClient _httpClient;

    public SidecarManager(
        string? appDataBinDir = null,
        string? appBaseBinDir = null,
        HttpClient? httpClient = null)
    {
        AppDataBinDir = appDataBinDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VortexDownloader",
            "bin");

        AppBaseBinDir = appBaseBinDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin");
        _httpClient = httpClient ?? new HttpClient();
    }

    public string? GetYtDlpPath() => ResolveExecutable("yt-dlp.exe");

    public string? GetFfmpegPath() => ResolveExecutable("ffmpeg.exe");

    public string? ResolveExecutable(string exeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exeName);
        if (Path.GetFileName(exeName) != exeName)
        {
            throw new ArgumentException("Yalnızca çalıştırılabilir dosya adı belirtilmelidir.", nameof(exeName));
        }

        // 1. %APPDATA%\VortexDownloader\bin\
        var appDataCandidate = Path.Combine(AppDataBinDir, exeName);
        if (File.Exists(appDataCandidate))
        {
            return Path.GetFullPath(appDataCandidate);
        }

        // 2. Uygulama çalışma dizinindeki bin\ veya çalışma dizini
        var appBaseBinCandidate = Path.Combine(AppBaseBinDir, exeName);
        if (File.Exists(appBaseBinCandidate))
        {
            return Path.GetFullPath(appBaseBinCandidate);
        }

        var appBaseCandidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName);
        if (File.Exists(appBaseCandidate))
        {
            return Path.GetFullPath(appBaseCandidate);
        }

        // 3. Sistem PATH ortam değişkeni
        var pathEnv = CustomPathEnvironment ?? Environment.GetEnvironmentVariable("PATH");
        var fromPath = FindInPath(exeName, pathEnv);
        if (!string.IsNullOrEmpty(fromPath))
        {
            return fromPath;
        }

        return null;
    }

    public static string? FindInPath(string exeName, string? pathEnv)
    {
        if (string.IsNullOrWhiteSpace(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in paths)
        {
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Geçersiz dizin karakterlerini güvenle atla
            }
        }

        return null;
    }

    public async Task EnsureSidecarsAsync(IProgress<string>? statusProgress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(AppDataBinDir);

        if (GetYtDlpPath() == null)
        {
            statusProgress?.Report("yt-dlp bulunamadı, indiriliyor...");
            var targetExe = Path.Combine(AppDataBinDir, "yt-dlp.exe");
            var tempFile = Path.Combine(AppDataBinDir, $"yt-dlp.exe.{Guid.NewGuid():N}.tmp");

            try
            {
                await DownloadFileAsync(YtDlpDownloadUrl, tempFile, statusProgress, "yt-dlp", ct);
                if (File.Exists(targetExe))
                {
                    File.Delete(targetExe);
                }
                File.Move(tempFile, targetExe);
                statusProgress?.Report("yt-dlp başarıyla hazırlandı.");
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { /* best effort cleanup */ }
                }
            }
        }
        else
        {
            statusProgress?.Report("yt-dlp hazır.");
        }

        if (GetFfmpegPath() == null)
        {
            statusProgress?.Report("FFmpeg bulunamadı, indiriliyor...");
            var tempZip = Path.Combine(AppDataBinDir, $"ffmpeg_{Guid.NewGuid():N}.zip");

            try
            {
                await DownloadFileAsync(FfmpegDownloadUrl, tempZip, statusProgress, "FFmpeg", ct);
                statusProgress?.Report("FFmpeg arşivi ayıklanıyor...");

                using (var zipStream = File.OpenRead(tempZip))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) ||
                            entry.Name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var destinationPath = Path.Combine(AppDataBinDir, entry.Name);
                            entry.ExtractToFile(destinationPath, overwrite: true);
                        }
                    }
                }

                statusProgress?.Report("FFmpeg başarıyla hazırlandı.");
            }
            finally
            {
                if (File.Exists(tempZip))
                {
                    try { File.Delete(tempZip); } catch { /* best effort cleanup */ }
                }
            }
        }
        else
        {
            statusProgress?.Report("FFmpeg hazır.");
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationFile,
        IProgress<string>? progress,
        string itemName,
        CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(
            destinationFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                var percent = (double)totalRead / totalBytes.Value * 100.0;
                progress?.Report($"{itemName} indiriliyor: %{percent:F1}");
            }
        }
    }
}
