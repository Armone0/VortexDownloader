using System.Globalization;
using System.Text.RegularExpressions;
using VortexDownloader.Core.Common;

namespace VortexDownloader.Core.Media;

public enum FfmpegPreset
{
    /// <summary>"MP4'e çevir (yeniden kodlamadan)": -c copy into .mp4.</summary>
    RemuxToMp4,
    /// <summary>"Sesi çıkar (M4A)": -vn -c:a copy, falling back to AAC when the codec does not fit M4A.</summary>
    ExtractAudioM4a
}

/// <summary>One-click ffmpeg jobs on finished files. Output goes next to the source with a unique name; the source is never touched.</summary>
public static class FfmpegPresets
{
    public const string RemuxIncompatibleMessage =
        "Bu dosyanın kodekleri MP4'e yeniden kodlamadan sığmıyor (ör. VP9/Opus ya da altyazı). Dosya dönüştürülmedi; kaynak değişmedi.";

    private static readonly Regex DurationRegex = new(@"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex OutTimeRegex = new(@"^out_time_(?:us|ms)=(\d+)\s*$", RegexOptions.Compiled);

    /// <summary>Argument list for ffmpeg (passed via ProcessStartInfo.ArgumentList, never a shell string). "-n" refuses to overwrite.</summary>
    public static IReadOnlyList<string> BuildArguments(FfmpegPreset preset, string input, string output, bool reencodeAudio = false)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-n", "-i", input };
        if (preset == FfmpegPreset.RemuxToMp4)
        {
            args.AddRange(["-c", "copy"]);
        }
        else
        {
            args.AddRange(reencodeAudio ? ["-vn", "-c:a", "aac", "-b:a", "192k"] : ["-vn", "-c:a", "copy"]);
        }
        args.AddRange(["-progress", "pipe:1", "-nostats", output]);
        return args;
    }

    public static string OutputPathFor(FfmpegPreset preset, string input, Func<string, bool> isTaken) =>
        FileCategories.UniqueFilePath(
            Path.ChangeExtension(input, preset == FfmpegPreset.RemuxToMp4 ? ".mp4" : ".m4a"),
            p => isTaken(p) || string.Equals(Path.GetFullPath(p), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase));

    /// <summary>Input duration from ffmpeg's stderr banner ("  Duration: 00:01:02.50, start: ...").</summary>
    public static bool TryParseDuration(string line, out TimeSpan duration)
    {
        duration = default;
        var m = DurationRegex.Match(line);
        if (!m.Success) return false;
        duration = TimeSpan.FromHours(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                 + TimeSpan.FromMinutes(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
                 + TimeSpan.FromSeconds(double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>Position from "-progress" output (out_time_us / out_time_ms, both microseconds).</summary>
    public static bool TryParseOutTime(string line, out TimeSpan position)
    {
        position = default;
        var m = OutTimeRegex.Match(line);
        if (!m.Success || !long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long us)) return false;
        position = TimeSpan.FromTicks(us * 10);
        return true;
    }

    /// <summary>
    /// Runs the preset and returns the output path. On failure or cancel the partial output is deleted.
    /// Throws <see cref="InvalidOperationException"/> with a user-facing message when ffmpeg fails.
    /// </summary>
    public static async Task<string> ConvertAsync(
        IProcessRunner runner, string ffmpegPath, FfmpegPreset preset, string input,
        Action<double>? onProgress = null, CancellationToken ct = default)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("Kaynak dosya bulunamadı.", input);

        string output = OutputPathFor(preset, input, File.Exists);
        try
        {
            var result = await RunOnceAsync(runner, ffmpegPath, BuildArguments(preset, input, output), onProgress, ct).ConfigureAwait(false);
            if (result.ExitCode == 0) return output;
            TryDelete(output);

            if (preset == FfmpegPreset.RemuxToMp4) throw new InvalidOperationException(RemuxIncompatibleMessage);

            // Audio codec does not fit M4A (e.g. Opus/Vorbis): encode to AAC instead.
            result = await RunOnceAsync(runner, ffmpegPath, BuildArguments(preset, input, output, reencodeAudio: true), onProgress, ct).ConfigureAwait(false);
            if (result.ExitCode == 0) return output;
            TryDelete(output);
            throw new InvalidOperationException($"Ses çıkarılamadı: {LastLine(result.StandardError)}");
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
            throw;
        }
    }

    private static Task<ProcessExecutionResult> RunOnceAsync(
        IProcessRunner runner, string ffmpegPath, IReadOnlyList<string> args, Action<double>? onProgress, CancellationToken ct)
    {
        TimeSpan duration = TimeSpan.Zero;
        return runner.RunAsync(
            ffmpegPath,
            args,
            onOutputLine: line =>
            {
                if (duration > TimeSpan.Zero && TryParseOutTime(line, out var pos))
                    onProgress?.Invoke(Math.Clamp(pos / duration * 100.0, 0, 100));
            },
            onErrorLine: line =>
            {
                if (duration == TimeSpan.Zero && TryParseDuration(line, out var d)) duration = d;
            },
            ct: ct);
    }

    private static string LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "bilinmeyen hata";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
