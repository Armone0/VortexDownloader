using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VortexDownloader.Core.Media;

public record ProcessExecutionResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        string fileName,
        string arguments,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken ct = default);
}

public class DefaultProcessRunner : IProcessRunner
{
    public static List<string> SplitCommandLine(string commandLine)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return list;

        var inQuotes = false;
        var token = new StringBuilder();

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (token.Length > 0)
                {
                    list.Add(token.ToString());
                    token.Clear();
                }
            }
            else
            {
                token.Append(c);
            }
        }

        if (token.Length > 0)
        {
            list.Add(token.ToString());
        }

        return list;
    }

    public Task<ProcessExecutionResult> RunAsync(
        string fileName,
        string arguments,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken ct = default)
    {
        return RunAsync(fileName, SplitCommandLine(arguments), onOutputLine, onErrorLine, ct);
    }

    public async Task<ProcessExecutionResult> RunAsync(
        string fileName,
        IEnumerable<string> argumentList,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in argumentList)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.Start();

        // Deadlock-free asenkron akış okuma: Output ve Error paralel task'lerde okunur.
        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                stdoutBuilder.AppendLine(line);
                onOutputLine?.Invoke(line);
            }
        }, ct);

        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct) is { } line)
            {
                stderrBuilder.AppendLine(line);
                onErrorLine?.Invoke(line);
            }
        }, ct);

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Process sonlandırma istisnasını yut
            }
            throw;
        }

        return new ProcessExecutionResult(process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }
}

public class YtDlpService
{
    private static readonly Regex DownloadProgressRegex = new(
        @"\[download\]\s+(?<percent>\d+(?:\.\d+)?)%\s+of\s+~?(?<total>[0-9.]+\s*[kMGTP]?i?B)(?:(?:\s+in\s+[0-9:]+)?(?:\s+at\s+(?<speed>[^\s]+))?)?(?:\s+ETA\s+(?<eta>[^\s]+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RemuxingRegex = new(
        @"\[(Merger|ffmpeg|ExtractAudio|Fixup[a-zA-Z0-9]*|EmbedSubtitle)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SidecarManager _sidecarManager;
    private readonly IProcessRunner _processRunner;

    public YtDlpService(SidecarManager? sidecarManager = null, IProcessRunner? processRunner = null)
    {
        _sidecarManager = sidecarManager ?? new SidecarManager();
        _processRunner = processRunner ?? new DefaultProcessRunner();
    }

    private static readonly HashSet<string> AllowedAudioFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "m4a", "flac", "opus", "wav", "aac"
    };

    public static string NormalizeAudioFormat(string? rawFormat)
    {
        if (string.IsNullOrWhiteSpace(rawFormat)) return "mp3";
        var trimmed = rawFormat.Trim().ToLowerInvariant();
        return AllowedAudioFormats.Contains(trimmed) ? trimmed : "mp3";
    }

    public static string SanitizeUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"Geçersiz medya URL'si: '{url}'. Yalnızca HTTP ve HTTPS şemaları desteklenir.", nameof(url));
        }

        return url.Replace("\"", "").Replace("\r", "").Replace("\n", "").Trim();
    }

    public async Task<MediaMetadata> InspectMediaAsync(string url, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var cleanUrl = SanitizeUrl(url);

        var ytDlpPath = _sidecarManager.GetYtDlpPath();
        if (string.IsNullOrEmpty(ytDlpPath))
        {
            throw new FileNotFoundException("yt-dlp çalıştırılabilir dosyası bulunamadı. Lütfen önce EnsureSidecarsAsync çağırın.");
        }

        var arguments = $"--dump-single-json --no-warnings --no-playlist -- \"{cleanUrl}\"";
        var result = await _processRunner.RunAsync(ytDlpPath, arguments, ct: ct);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"yt-dlp analizi başarısız oldu (Hata Kodu {result.ExitCode}): {result.StandardError.Trim()}");
        }

        return MediaMetadata.FromJson(result.StandardOutput);
    }

    public async Task DownloadMediaAsync(
        MediaDownloadOptions options,
        IProgress<MediaProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Url);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DestinationPath);
        SanitizeUrl(options.Url);

        var ytDlpPath = _sidecarManager.GetYtDlpPath();
        if (string.IsNullOrEmpty(ytDlpPath))
        {
            throw new FileNotFoundException("yt-dlp çalıştırılabilir dosyası bulunamadı. Lütfen önce EnsureSidecarsAsync çağırın.");
        }

        var ffmpegPath = _sidecarManager.GetFfmpegPath();
        var arguments = BuildDownloadArguments(options, ffmpegPath);

        progress?.Report(new MediaProgressInfo
        {
            Status = MediaDownloadStatus.Inspecting,
            Percentage = 0.0
        });

        var result = await _processRunner.RunAsync(
            ytDlpPath,
            arguments,
            onOutputLine: line =>
            {
                if (TryParseProgressLine(line, out var progressInfo))
                {
                    progress?.Report(progressInfo);
                }
            },
            onErrorLine: line =>
            {
                if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report(new MediaProgressInfo
                    {
                        Status = MediaDownloadStatus.Error,
                        SpeedText = line
                    });
                }
            },
            ct: ct);

        if (result.ExitCode != 0)
        {
            progress?.Report(new MediaProgressInfo
            {
                Status = MediaDownloadStatus.Error,
                SpeedText = result.StandardError.Trim()
            });

            throw new InvalidOperationException($"Medya indirme işlemi başarısız oldu (Hata Kodu {result.ExitCode}): {result.StandardError.Trim()}");
        }

        progress?.Report(new MediaProgressInfo
        {
            Status = MediaDownloadStatus.Completed,
            Percentage = 100.0,
            SpeedText = "Tamamlandı"
        });
    }

    public static string BuildDownloadArguments(MediaDownloadOptions options, string? ffmpegPath = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Url);

        var cleanUrl = SanitizeUrl(options.Url);

        var args = new StringBuilder();
        args.Append("--newline --progress --no-playlist ");
        args.Append($"--concurrent-fragments {Math.Max(1, options.ConcurrentFragments)} ");

        if (options.ExtractAudioOnly)
        {
            var audioFormat = NormalizeAudioFormat(options.AudioFormat);
            args.Append($"-x --audio-format {audioFormat} --audio-quality 0 ");
        }
        else
        {
            var formatSelection = ResolveFormatSelector(options.SelectedFormatId);
            args.Append($"-f \"{formatSelection}\" --merge-output-format mp4 ");
        }

        if (!string.IsNullOrWhiteSpace(ffmpegPath))
        {
            args.Append($"--ffmpeg-location \"{ffmpegPath}\" ");
        }

        if (options.DownloadSubtitles)
        {
            args.Append("--write-subs --sub-langs all --embed-subs ");
        }

        args.Append($"-o \"{options.DestinationPath}\" ");
        args.Append($"-- \"{cleanUrl}\"");

        return args.ToString();
    }

    public static string ResolveFormatSelector(string? selectedFormatId)
    {
        if (string.IsNullOrWhiteSpace(selectedFormatId))
        {
            return "bestvideo+bestaudio/best";
        }

        var trimmed = selectedFormatId.Trim();

        // örn. "1080p", "720p", "480p", "360p"
        if (trimmed.EndsWith("p", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(trimmed[..^1], out var height) && height > 0)
        {
            return $"bestvideo[height<={height}]+bestaudio/best[height<={height}]/best";
        }

        return trimmed;
    }

    public static bool TryParseProgressLine(string line, out MediaProgressInfo progressInfo)
    {
        progressInfo = new MediaProgressInfo();
        if (string.IsNullOrWhiteSpace(line)) return false;

        var downloadMatch = DownloadProgressRegex.Match(line);
        if (downloadMatch.Success)
        {
            var percentStr = downloadMatch.Groups["percent"].Value;
            var totalStr = downloadMatch.Groups["total"].Value;
            var speedStr = downloadMatch.Groups["speed"].Value;
            var etaStr = downloadMatch.Groups["eta"].Value;

            double.TryParse(percentStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent);
            var totalBytes = ParseByteSize(totalStr);
            var downloadedBytes = totalBytes > 0
                ? (long)Math.Round(totalBytes * (percent / 100.0))
                : 0L;

            progressInfo.Percentage = percent;
            progressInfo.TotalBytes = totalBytes;
            progressInfo.DownloadedBytes = downloadedBytes;
            progressInfo.SpeedText = string.IsNullOrWhiteSpace(speedStr) ? string.Empty : speedStr;
            progressInfo.EtaText = string.IsNullOrWhiteSpace(etaStr) ? string.Empty : etaStr;
            progressInfo.Status = percent >= 100.0 ? MediaDownloadStatus.Downloading : MediaDownloadStatus.Downloading;
            return true;
        }

        if (RemuxingRegex.IsMatch(line))
        {
            progressInfo.Status = MediaDownloadStatus.Remuxing;
            progressInfo.Percentage = 99.0;
            progressInfo.SpeedText = "Dönüştürülüyor / Birleştiriliyor...";
            return true;
        }

        if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            progressInfo.Status = MediaDownloadStatus.Error;
            progressInfo.SpeedText = line.Trim();
            return true;
        }

        return false;
    }

    public static long ParseByteSize(string sizeStr)
    {
        if (string.IsNullOrWhiteSpace(sizeStr)) return 0;

        var match = Regex.Match(sizeStr.Trim(), @"^~?([0-9.]+)\s*([kMGTP]?i?B)$", RegexOptions.IgnoreCase);
        if (!match.Success) return 0;

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        var unit = match.Groups[2].Value.ToUpperInvariant();
        return unit switch
        {
            "B" => (long)value,
            "KB" => (long)(value * 1000),
            "KIB" => (long)(value * 1024),
            "MB" => (long)(value * 1000 * 1000),
            "MIB" => (long)(value * 1024 * 1024),
            "GB" => (long)(value * 1000 * 1000 * 1000),
            "GIB" => (long)(value * 1024 * 1024 * 1024),
            "TB" => (long)(value * 1000L * 1000L * 1000L * 1000L),
            "TIB" => (long)(value * 1024L * 1024L * 1024L * 1024L),
            _ => (long)value
        };
    }
}
