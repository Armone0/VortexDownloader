using System.Diagnostics;
using System.IO.Compression;

namespace VortexDownloader.Core.Media;

public class SidecarManager
{
    public const string DefaultYtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string DefaultFfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    public const string FallbackFfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

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
        
        if (httpClient != null)
        {
            _httpClient = httpClient;
        }
        else
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) VortexDownloader/1.6");
        }
    }

    public Func<string, bool>? FfmpegValidator { get; set; } = IsValidFfmpeg;

    public string? GetYtDlpPath() => ResolveExecutable("yt-dlp.exe");

    public string? GetFfmpegPath()
    {
        // 1. %APPDATA%\VortexDownloader\bin\
        var appDataCandidate = Path.Combine(AppDataBinDir, "ffmpeg.exe");
        if (File.Exists(appDataCandidate))
        {
            return Path.GetFullPath(appDataCandidate);
        }

        // 2. Uygulama çalışma dizinindeki bin\ veya çalışma dizini
        var appBaseBinCandidate = Path.Combine(AppBaseBinDir, "ffmpeg.exe");
        if (File.Exists(appBaseBinCandidate))
        {
            return Path.GetFullPath(appBaseBinCandidate);
        }

        var appBaseCandidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(appBaseCandidate))
        {
            return Path.GetFullPath(appBaseCandidate);
        }

        // 3. Sistem PATH ortam değişkeni (yalnızca modern ve -hide_banner destekleyen FFmpeg kabul edilir)
        var pathEnv = CustomPathEnvironment ?? Environment.GetEnvironmentVariable("PATH");
        var fromPath = FindInPath("ffmpeg.exe", pathEnv, FfmpegValidator);
        if (!string.IsNullOrEmpty(fromPath))
        {
            return fromPath;
        }

        return null;
    }

    public static bool IsValidFfmpeg(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = "-hide_banner -version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

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

    public static string? FindInPath(string exeName, string? pathEnv, Func<string, bool>? validator = null)
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
                    var full = Path.GetFullPath(candidate);
                    if (validator == null || validator(full))
                    {
                        return full;
                    }
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
            statusProgress?.Report("FFmpeg bulunamadı veya güncel değil, indiriliyor...");
            var tempZip = Path.Combine(AppDataBinDir, $"ffmpeg_{Guid.NewGuid():N}.zip");

            try
            {
                try
                {
                    await DownloadFileAsync(FfmpegDownloadUrl, tempZip, statusProgress, "FFmpeg", ct);
                }
                catch
                {
                    statusProgress?.Report("Birincil FFmpeg indirme kaynağı başarısız oldu, yedek kaynaktan deneniyor...");
                    await DownloadFileAsync(FallbackFfmpegUrl, tempZip, statusProgress, "FFmpeg", ct);
                }

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
