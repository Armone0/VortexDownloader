using System.Text.Json;
using VortexDownloader.Core.Media;
using Xunit;

namespace VortexDownloader.Tests;

public class MediaTests
{
    private const string SampleYtDlpJson = """
    {
      "id": "dQw4w9WgXcQ",
      "title": "Rick Astley - Never Gonna Give You Up (Official Music Video)",
      "webpage_url": "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
      "thumbnail": "https://i.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg",
      "duration": 213.5,
      "uploader": "Rick Astley",
      "description": "The official video for Never Gonna Give You Up by Rick Astley.",
      "formats": [
        {
          "format_id": "140",
          "ext": "m4a",
          "resolution": "audio only",
          "vcodec": "none",
          "acodec": "mp4a.40.2",
          "filesize": 3546820,
          "format_note": "medium"
        },
        {
          "format_id": "137",
          "ext": "mp4",
          "resolution": "1920x1080",
          "height": 1080,
          "width": 1920,
          "vcodec": "avc1.640028",
          "acodec": "none",
          "filesize_approx": 48200000,
          "format_note": "1080p60"
        },
        {
          "format_id": "136",
          "ext": "mp4",
          "resolution": "1280x720",
          "height": 720,
          "width": 1280,
          "vcodec": "avc1.4d401f",
          "acodec": "none",
          "filesize": 25000000,
          "format_note": "720p"
        },
        {
          "format_id": "18",
          "ext": "mp4",
          "resolution": "640x360",
          "height": 360,
          "width": 640,
          "vcodec": "avc1.42001E",
          "acodec": "mp4a.40.2",
          "filesize": 15000000,
          "format_note": "360p"
        }
      ]
    }
    """;

    private class MockProcessRunner : IProcessRunner
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = string.Empty;
        public string StandardError { get; set; } = string.Empty;
        public List<string> OutputLinesToEmit { get; set; } = [];
        public List<string> ErrorLinesToEmit { get; set; } = [];
        public string? LastFileName { get; private set; }
        public string? LastArguments { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(
            string fileName,
            string arguments,
            Action<string>? onOutputLine = null,
            Action<string>? onErrorLine = null,
            CancellationToken ct = default)
        {
            LastFileName = fileName;
            LastArguments = arguments;

            foreach (var line in OutputLinesToEmit)
            {
                onOutputLine?.Invoke(line);
            }

            foreach (var line in ErrorLinesToEmit)
            {
                onErrorLine?.Invoke(line);
            }

            return Task.FromResult(new ProcessExecutionResult(ExitCode, StandardOutput, StandardError));
        }
    }

    [Fact]
    public void MediaMetadata_FromJson_CorrectlyDeserializesYtDlpOutput()
    {
        var metadata = MediaMetadata.FromJson(SampleYtDlpJson);

        Assert.NotNull(metadata);
        Assert.Equal("dQw4w9WgXcQ", metadata.Id);
        Assert.Equal("Rick Astley - Never Gonna Give You Up (Official Music Video)", metadata.Title);
        Assert.Equal("https://www.youtube.com/watch?v=dQw4w9WgXcQ", metadata.WebpageUrl);
        Assert.Equal("https://i.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg", metadata.ThumbnailUrl);
        Assert.Equal(213.5, metadata.DurationSeconds);
        Assert.Equal("Rick Astley", metadata.Uploader);
        Assert.Contains("Never Gonna Give You Up", metadata.Description);
        Assert.Equal(4, metadata.AvailableFormats.Count);

        // Format 1: Audio only
        var audioFormat = metadata.AvailableFormats[0];
        Assert.Equal("140", audioFormat.FormatId);
        Assert.Equal("m4a", audioFormat.Extension);
        Assert.Equal("audio only", audioFormat.Resolution);
        Assert.True(audioFormat.IsAudioOnly);
        Assert.False(audioFormat.IsVideoOnly);
        Assert.Equal(3546820, audioFormat.FilesizeApprox);
        Assert.Equal("medium", audioFormat.Note);

        // Format 2: 1080p Video Only
        var video1080 = metadata.AvailableFormats[1];
        Assert.Equal("137", video1080.FormatId);
        Assert.Equal("mp4", video1080.Extension);
        Assert.Equal("1080p", video1080.Resolution);
        Assert.True(video1080.IsVideoOnly);
        Assert.False(video1080.IsAudioOnly);
        Assert.Equal(48200000, video1080.FilesizeApprox);
        Assert.Equal("1080p60", video1080.Note);

        // Format 4: Muxed 360p (Both video and audio)
        var muxed360 = metadata.AvailableFormats[3];
        Assert.Equal("18", muxed360.FormatId);
        Assert.Equal("360p", muxed360.Resolution);
        Assert.False(muxed360.IsVideoOnly);
        Assert.False(muxed360.IsAudioOnly);
    }

    [Fact]
    public void MediaMetadata_FromJson_EmptyOrInvalidThrows()
    {
        Assert.Throws<ArgumentException>(() => MediaMetadata.FromJson(""));
        Assert.Throws<ArgumentException>(() => MediaMetadata.FromJson("   "));
        Assert.ThrowsAny<JsonException>(() => MediaMetadata.FromJson("{ invalid json }"));
    }

    [Theory]
    [InlineData("1080p", "bestvideo[height<=1080][format_note!*=?Premium]+bestaudio/bestvideo[height<=1080]+bestaudio/best[height<=1080]/best")]
    [InlineData("720p", "bestvideo[height<=720][format_note!*=?Premium]+bestaudio/bestvideo[height<=720]+bestaudio/best[height<=720]/best")]
    [InlineData("480p", "bestvideo[height<=480][format_note!*=?Premium]+bestaudio/bestvideo[height<=480]+bestaudio/best[height<=480]/best")]
    [InlineData("bestvideo+bestaudio", "bestvideo[format_note!*=?Premium]+bestaudio")]
    [InlineData("137+140", "137+140")]
    [InlineData("", "bestvideo[format_note!*=?Premium]+bestaudio/bestvideo+bestaudio/best")]
    [InlineData(null, "bestvideo[format_note!*=?Premium]+bestaudio/bestvideo+bestaudio/best")]
    public void ResolveFormatSelector_ResolvesResolutionOrCustomFormatId(string? input, string expected)
    {
        var result = YtDlpService.ResolveFormatSelector(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildDownloadArguments_VideoDownload_BuildsCorrectCommandString()
    {
        var options = new MediaDownloadOptions
        {
            Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            DestinationPath = @"C:\Downloads\NeverGonnaGiveYouUp.mp4",
            SelectedFormatId = "1080p",
            ExtractAudioOnly = false,
            ConcurrentFragments = 16,
            DownloadSubtitles = true
        };

        var ffmpegPath = @"C:\Tools\ffmpeg.exe";
        var args = YtDlpService.BuildDownloadArguments(options, ffmpegPath);

        Assert.Contains("--newline", args);
        Assert.Contains("--progress", args);
        Assert.Contains("--concurrent-fragments 16", args);
        Assert.Contains("-f \"bestvideo[height<=1080][format_note!*=?Premium]+bestaudio/bestvideo[height<=1080]+bestaudio/best[height<=1080]/best\"", args);
        Assert.Contains("--merge-output-format mp4", args);
        Assert.Contains(@"--ffmpeg-location ""C:\Tools\ffmpeg.exe""", args);
        Assert.Contains("--write-subs --sub-langs all --embed-subs", args);
        Assert.Contains(@"-o ""C:\Downloads\NeverGonnaGiveYouUp.mp4""", args);
        Assert.Contains(@"""https://www.youtube.com/watch?v=dQw4w9WgXcQ""", args);
    }

    [Fact]
    public void BuildDownloadArguments_AudioOnlyExtraction_BuildsAudioParameters()
    {
        var options = new MediaDownloadOptions
        {
            Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            DestinationPath = @"C:\Downloads\RickAstley.mp3",
            ExtractAudioOnly = true,
            AudioFormat = "mp3",
            ConcurrentFragments = 8,
            DownloadSubtitles = false
        };

        var args = YtDlpService.BuildDownloadArguments(options, null);

        Assert.Contains("-x --audio-format mp3 --audio-quality 0", args);
        Assert.DoesNotContain("--merge-output-format mp4", args);
        Assert.DoesNotContain("--ffmpeg-location", args);
        Assert.DoesNotContain("--write-subs", args);
        Assert.Contains("--concurrent-fragments 8", args);
    }

    [Theory]
    [InlineData("1024B", 1024L)]
    [InlineData("15.5KB", 15500L)]
    [InlineData("100KiB", 102400L)]
    [InlineData("120.5MiB", 126353408L)]
    [InlineData("1.5GiB", 1610612736L)]
    [InlineData("~50.00MiB", 52428800L)]
    [InlineData("invalid", 0L)]
    public void ParseByteSize_CorrectlyCalculatesBytes(string input, long expectedBytes)
    {
        var actual = YtDlpService.ParseByteSize(input);
        Assert.Equal(expectedBytes, actual);
    }

    [Fact]
    public void TryParseProgressLine_StandardDownloadLine_ParsesCorrectly()
    {
        var line = "[download]  45.2% of ~120.5MiB at 15.2MiB/s ETA 00:04";
        var success = YtDlpService.TryParseProgressLine(line, out var info);

        Assert.True(success);
        Assert.Equal(45.2, info.Percentage);
        Assert.Equal(126353408L, info.TotalBytes);
        Assert.True(info.DownloadedBytes > 0);
        Assert.Equal("15.2MiB/s", info.SpeedText);
        Assert.Equal("00:04", info.EtaText);
        Assert.Equal(MediaDownloadStatus.Downloading, info.Status);
    }

    [Fact]
    public void TryParseProgressLine_CompletedLine_Parses100Percent()
    {
        var line = "[download] 100% of 120.50MiB in 00:08 at 14.88MiB/s";
        var success = YtDlpService.TryParseProgressLine(line, out var info);

        Assert.True(success);
        Assert.Equal(100.0, info.Percentage);
        Assert.Equal("14.88MiB/s", info.SpeedText);
        Assert.Equal(info.TotalBytes, info.DownloadedBytes);
    }

    [Theory]
    [InlineData("[Merger] Merging formats into \"video.mp4\"")]
    [InlineData("[ffmpeg] Merging formats into \"video.mp4\"")]
    [InlineData("[ExtractAudio] Destination: song.mp3")]
    [InlineData("[FixupM4a] Correcting container in \"song.m4a\"")]
    [InlineData("[EmbedSubtitle] Embedding subtitles in \"video.mp4\"")]
    public void TryParseProgressLine_RemuxingLines_ReportsRemuxingStatus(string line)
    {
        var success = YtDlpService.TryParseProgressLine(line, out var info);

        Assert.True(success);
        Assert.Equal(MediaDownloadStatus.Remuxing, info.Status);
        Assert.Equal(99.0, info.Percentage);
    }

    [Fact]
    public void TryParseProgressLine_ErrorLine_ReportsErrorStatus()
    {
        var line = "ERROR: [youtube] dQw4w9WgXcQ: Video unavailable";
        var success = YtDlpService.TryParseProgressLine(line, out var info);

        Assert.True(success);
        Assert.Equal(MediaDownloadStatus.Error, info.Status);
        Assert.Contains("ERROR:", info.SpeedText);
    }

    [Fact]
    public void TryParseProgressLine_IrrelevantLine_ReturnsFalse()
    {
        var line = "[youtube] Extracting URL: https://www.youtube.com/watch?v=dQw4w9WgXcQ";
        var success = YtDlpService.TryParseProgressLine(line, out _);

        Assert.False(success);
    }

    [Fact]
    public void SidecarManager_ResolvesFromAppData_WhenFileExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"VortexTest_Sidecar_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dummyYtDlp = Path.Combine(tempDir, "yt-dlp.exe");
            File.WriteAllText(dummyYtDlp, "dummy binary");

            var manager = new SidecarManager(appDataBinDir: tempDir);
            var resolved = manager.GetYtDlpPath();

            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(dummyYtDlp), resolved);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public void SidecarManager_ResolvesFromAppBase_WhenNotInAppData()
    {
        var emptyAppData = Path.Combine(Path.GetTempPath(), $"VortexTest_Empty_{Guid.NewGuid():N}");
        var tempAppBase = Path.Combine(Path.GetTempPath(), $"VortexTest_Base_{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyAppData);
        Directory.CreateDirectory(tempAppBase);

        try
        {
            var dummyFfmpeg = Path.Combine(tempAppBase, "ffmpeg.exe");
            File.WriteAllText(dummyFfmpeg, "dummy ffmpeg");

            var manager = new SidecarManager(appDataBinDir: emptyAppData, appBaseBinDir: tempAppBase);
            var resolved = manager.GetFfmpegPath();

            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(dummyFfmpeg), resolved);
        }
        finally
        {
            try { Directory.Delete(emptyAppData, true); } catch { }
            try { Directory.Delete(tempAppBase, true); } catch { }
        }
    }

    [Fact]
    public void SidecarManager_FindInPath_ResolvesFromCustomPathEnv()
    {
        var tempPathDir = Path.Combine(Path.GetTempPath(), $"VortexTest_Path_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPathDir);

        try
        {
            var testExe = Path.Combine(tempPathDir, "testtool.exe");
            File.WriteAllText(testExe, "tool");

            var resolved = SidecarManager.FindInPath("testtool.exe", tempPathDir);
            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(testExe), resolved);

            var notFound = SidecarManager.FindInPath("nonexistent.exe", tempPathDir);
            Assert.Null(notFound);
        }
        finally
        {
            try { Directory.Delete(tempPathDir, true); } catch { }
        }
    }

    [Fact]
    public void SidecarManager_FindInPath_WithValidator_RejectsWhenValidatorReturnsFalse()
    {
        var tempPathDir = Path.Combine(Path.GetTempPath(), $"VortexTest_PathVal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPathDir);

        try
        {
            var testExe = Path.Combine(tempPathDir, "bad_tool.exe");
            File.WriteAllText(testExe, "bad");

            var resolved = SidecarManager.FindInPath("bad_tool.exe", tempPathDir, _ => false);
            Assert.Null(resolved);

            var accepted = SidecarManager.FindInPath("bad_tool.exe", tempPathDir, _ => true);
            Assert.NotNull(accepted);
        }
        finally
        {
            try { Directory.Delete(tempPathDir, true); } catch { }
        }
    }

    [Fact]
    public async Task YtDlpService_InspectMediaAsync_SuccessFlow()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"VortexTest_Service_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dummyExe = Path.Combine(tempDir, "yt-dlp.exe");
            File.WriteAllText(dummyExe, "dummy");

            var sidecar = new SidecarManager(appDataBinDir: tempDir);
            var mockRunner = new MockProcessRunner
            {
                ExitCode = 0,
                StandardOutput = SampleYtDlpJson
            };

            var service = new YtDlpService(sidecar, mockRunner);
            var metadata = await service.InspectMediaAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ");

            Assert.NotNull(metadata);
            Assert.Equal("dQw4w9WgXcQ", metadata.Id);
            Assert.Contains("--dump-single-json", mockRunner.LastArguments!);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task YtDlpService_InspectMediaAsync_ThrowsWhenExitCodeNonZero()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"VortexTest_Service_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dummyExe = Path.Combine(tempDir, "yt-dlp.exe");
            File.WriteAllText(dummyExe, "dummy");

            var sidecar = new SidecarManager(appDataBinDir: tempDir);
            var mockRunner = new MockProcessRunner
            {
                ExitCode = 1,
                StandardError = "ERROR: Private video"
            };

            var service = new YtDlpService(sidecar, mockRunner);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.InspectMediaAsync("https://www.youtube.com/watch?v=private"));

            Assert.Contains("ERROR: Private video", ex.Message);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task YtDlpService_DownloadMediaAsync_StreamsProgressEvents()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"VortexTest_Service_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dummyExe = Path.Combine(tempDir, "yt-dlp.exe");
            File.WriteAllText(dummyExe, "dummy");

            var sidecar = new SidecarManager(appDataBinDir: tempDir);
            var mockRunner = new MockProcessRunner
            {
                ExitCode = 0,
                OutputLinesToEmit =
                [
                    "[download]  25.0% of ~100.0MiB at 10.0MiB/s ETA 00:10",
                    "[download]  50.0% of ~100.0MiB at 12.0MiB/s ETA 00:05",
                    "[Merger] Merging formats into \"C:\\test.mp4\"",
                    "[download] 100% of 100.0MiB in 00:08 at 12.5MiB/s"
                ]
            };

            var service = new YtDlpService(sidecar, mockRunner);
            var reportedProgress = new List<MediaProgressInfo>();
            var progress = new Progress<MediaProgressInfo>(p => reportedProgress.Add(p));

            var options = new MediaDownloadOptions
            {
                Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
                DestinationPath = @"C:\test.mp4",
                SelectedFormatId = "1080p"
            };

            await service.DownloadMediaAsync(options, progress);

            // Give Progress<T> event pump a brief moment
            await Task.Delay(50);

            Assert.NotEmpty(reportedProgress);
            Assert.Contains(reportedProgress, p => p.Status == MediaDownloadStatus.Completed);
            Assert.Contains(reportedProgress, p => p.Status == MediaDownloadStatus.Downloading && p.Percentage == 25.0);
            Assert.Contains(reportedProgress, p => p.Status == MediaDownloadStatus.Remuxing);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SidecarManager_ResolveExecutable_ThrowsWhenPathContainsDirectorySeparators()
    {
        var manager = new SidecarManager();
        Assert.Throws<ArgumentException>(() => manager.ResolveExecutable(@"..\..\malicious.exe"));
        Assert.Throws<ArgumentException>(() => manager.ResolveExecutable(@"subdir/tool.exe"));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("http://example.com/video.mp4")]
    public void YtDlpService_SanitizeUrl_AllowsValidHttpAndHttps(string url)
    {
        var sanitized = YtDlpService.SanitizeUrl(url);
        Assert.Equal(url, sanitized);
    }

    [Theory]
    [InlineData("ftp://example.com/video.mp4")]
    [InlineData("file:///C:/secrets.txt")]
    [InlineData("not-a-valid-url")]
    [InlineData("")]
    public void YtDlpService_SanitizeUrl_ThrowsForInvalidOrNonHttpScheme(string url)
    {
        Assert.Throws<ArgumentException>(() => YtDlpService.SanitizeUrl(url));
    }

    [Theory]
    [InlineData("mp3", "mp3")]
    [InlineData("M4A", "m4a")]
    [InlineData("flac", "flac")]
    [InlineData("opus", "opus")]
    [InlineData("wav", "wav")]
    [InlineData("aac", "aac")]
    [InlineData("exe", "mp3")]
    [InlineData("sh", "mp3")]
    [InlineData("", "mp3")]
    [InlineData(null, "mp3")]
    public void YtDlpService_NormalizeAudioFormat_EnforcesWhitelist(string? input, string expected)
    {
        var actual = YtDlpService.NormalizeAudioFormat(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void YtDlpService_BuildDownloadArguments_IncludesDoubleDashBeforeUrl()
    {
        var options = new MediaDownloadOptions
        {
            Url = "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            DestinationPath = @"C:\Downloads\Video.mp4",
            AudioFormat = "mp3"
        };

        var args = YtDlpService.BuildDownloadArguments(options);
        Assert.Contains(@"-- ""https://www.youtube.com/watch?v=dQw4w9WgXcQ""", args);
    }

    [Fact]
    public void DefaultProcessRunner_SplitCommandLine_CorrectlyParsesQuotedArguments()
    {
        var cmd = @"--newline -f ""bestvideo+bestaudio"" -o ""C:\Test Path\output.mp4"" -- ""https://test.com/vid""";
        var parts = DefaultProcessRunner.SplitCommandLine(cmd);

        Assert.Equal(["--newline", "-f", "bestvideo+bestaudio", "-o", @"C:\Test Path\output.mp4", "--", "https://test.com/vid"], parts);
    }
}
