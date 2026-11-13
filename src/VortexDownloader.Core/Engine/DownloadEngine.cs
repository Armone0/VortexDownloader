using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using VortexDownloader.Core.Common;
using VortexDownloader.Core.Models;
using VortexDownloader.Core.Network;
using VortexDownloader.Core.Storage;

namespace VortexDownloader.Core.Engine;

public class DownloadEngine : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly StateStore _stateStore;
    private readonly SpeedLimiter _globalSpeedLimiter;
    private readonly ConcurrentDictionary<Guid, DownloadTask> _tasks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningTokens = new();
    private readonly ConcurrentDictionary<Guid, Task> _runningExecutionTasks = new();
    private readonly ConcurrentDictionary<Guid, long> _lastDownloadedBytes = new();
    private readonly Timer _monitorTimer;
    private long _lastMonitorTicks;
    private int _stateSaveTickCounter;
    private const int StateSaveIntervalTicks = 10; // Save state at most once every 5 seconds (10 * 500ms) during active downloads
    private bool _isDisposed;

    // Post-download integrity checks (SHA-256 + Defender), one file at a time.
    private readonly DefenderScanner _scanner;
    private readonly SemaphoreSlim _postCheckGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _postCheckTokens = new();
    private readonly ConcurrentDictionary<Guid, Task> _postCheckTasks = new();

    public bool ScanWithDefender { get; set; } = true;

    /// <summary>
    /// "Dosya türüne göre klasörle": returns the default download folder when the option is on, else null.
    /// New downloads saved directly in that folder go to a category subfolder instead.
    /// </summary>
    public Func<string?>? AutoFolderRoot { get; set; }
    public event Action<DownloadTask>? ThreatDetected;

    public event Action<DownloadTask>? TaskAdded;
    public event Action<DownloadTask>? TaskUpdated;
    public event Action<DownloadTask>? TaskCompleted;
    public event Action<DownloadTask, string>? TaskFailed;
    public event Action<DownloadTask>? TaskRemoved;

    public SpeedLimiter GlobalSpeedLimiter => _globalSpeedLimiter;
    public IReadOnlyCollection<DownloadTask> Tasks => _tasks.Values.ToList();

    private readonly object _queueLock = new();
    private volatile int _maxConcurrentDownloads = 3;

    public int MaxConcurrentDownloads
    {
        get => _maxConcurrentDownloads;
        set
        {
            _maxConcurrentDownloads = value;
            ProcessDownloadQueue();
        }
    }

    public DownloadEngine(StateStore? stateStore = null, HttpClient? httpClient = null, long globalSpeedLimit = 0, DefenderScanner? scanner = null)
    {
        _stateStore = stateStore ?? new StateStore();
        _scanner = scanner ?? new DefenderScanner();
        _httpClient = httpClient ?? ChunkDownloader.CreateOptimizedHttpClient();
        _globalSpeedLimiter = new SpeedLimiter(globalSpeedLimit);

        _lastMonitorTicks = Stopwatch.GetTimestamp();
        _monitorTimer = new Timer(OnMonitorTick, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    public async Task InitializeAsync()
    {
        var savedTasks = await _stateStore.LoadTasksAsync().ConfigureAwait(false);
        bool migrated = false;
        foreach (var task in savedTasks)
        {
            // Older versions kept user:pass@ in the URL (plain text in tasks.json, shown on cards): move it to the protected login.
            string clean = HttpCredential.StripUserInfo(task.Url, out var infoUser, out var infoPassword);
            if (infoUser != null)
            {
                task.Url = clean;
                if (string.IsNullOrEmpty(task.AuthUser))
                {
                    task.AuthUser = infoUser;
                    task.AuthPassword = infoPassword;
                    task.AuthOrigin = HttpCredential.OriginOf(clean);
                }
                migrated = true;
            }

            _tasks[task.Id] = task;
            TaskAdded?.Invoke(task);

            // Check interrupted by app close: run it again.
            if (task.Status == DownloadStatus.Completed && task.ScanStatus == ScanStatus.Scanning)
            {
                StartPostDownloadChecks(task);
            }
        }
        if (migrated) await SaveStateAsync().ConfigureAwait(false);
    }

    public async Task<UrlMetadata> InspectUrlAsync(
        string url,
        CancellationToken cancellationToken = default,
        string? cookies = null,
        string? referrer = null,
        string? userAgent = null,
        string? authUser = null,
        string? authPassword = null)
    {
        url = HttpCredential.StripUserInfo(url, out var infoUser, out var infoPassword);
        var credential = string.IsNullOrEmpty(authUser)
            ? HttpCredential.For(url, infoUser, infoPassword)
            : HttpCredential.For(url, authUser, authPassword);
        return await UrlMetadataInspector.InspectAsync(
            _httpClient, url, cancellationToken, cookies, referrer, userAgent, credential).ConfigureAwait(false);
    }

    public async Task<DownloadTask> AddDownloadAsync(
        string url,
        string destinationPath,
        int chunkCount = 8,
        bool autoStart = true,
        UrlMetadata? preloadedMetadata = null,
        string? cookies = null,
        string? referrer = null,
        string? userAgent = null,
        string? sourcePageUrl = null,
        IEnumerable<string>? mirrorUrls = null,
        DateTime? startAt = null,
        string? expectedSha256 = null,
        string? authUser = null,
        string? authPassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        // user:pass@ never stays in the URL (cards, tasks.json, logs): it becomes the task's credential instead.
        url = HttpCredential.StripUserInfo(url, out var infoUser, out var infoPassword);
        if (string.IsNullOrEmpty(authUser)) (authUser, authPassword) = (infoUser, infoPassword);
        var credential = HttpCredential.For(url, authUser, authPassword);

        // Fetch URL metadata only if not preloaded (preserves single-use tokens!)
        var metadata = preloadedMetadata ?? await UrlMetadataInspector.InspectAsync(
            _httpClient, url, CancellationToken.None, cookies, referrer, userAgent, credential).ConfigureAwait(false);

        string fileName = Path.GetFileName(destinationPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = metadata.SuggestedFileName;
            destinationPath = Path.Combine(destinationPath, fileName);
        }

        if (AutoFolderRoot?.Invoke() is { } autoRoot)
        {
            // Unique against files on disk and other tasks' targets: never overwrite.
            string resolved = FileCategories.ResolveAutoFolderPath(destinationPath, autoRoot, p =>
                File.Exists(p) || _tasks.Values.Any(t => string.Equals(t.DestinationPath, p, StringComparison.OrdinalIgnoreCase)));
            if (resolved != destinationPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
                destinationPath = resolved;
                fileName = Path.GetFileName(resolved);
            }
        }

        string tempDir = Path.Combine(
            Path.GetTempPath(),
            "VortexDownloader",
            Guid.NewGuid().ToString("N"));

        var task = new DownloadTask
        {
            Id = Guid.NewGuid(),
            Url = url,
            DestinationPath = destinationPath,
            FileName = fileName,
            TotalBytes = metadata.ContentLength,
            SupportsRange = metadata.SupportsRange,
            ETag = metadata.ETag,
            LastModified = metadata.LastModified,
            MaxChunks = Math.Clamp(chunkCount, 1, 32),
            TempDirectory = tempDir,
            Status = DownloadStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            Cookies = cookies,
            Referrer = referrer,
            UserAgent = userAgent,
            SourcePageUrl = sourcePageUrl ?? referrer,
            StartAt = startAt?.ToUniversalTime(),
            ExpectedSha256 = expectedSha256,
            AuthUser = credential?.User,
            AuthOrigin = credential?.Origin,
            AuthPassword = credential != null ? authPassword : null
        };

        // Initialize initial chunks
        int initialChunks = metadata.SupportsRange ? task.MaxChunks : 1;
        task.Chunks = ChunksManager.InitializeChunks(task.Id, task.TotalBytes, initialChunks, tempDir);

        if (metadata.SupportsRange)
        {
            task.MirrorUrls = NormalizeMirrorUrls(url, mirrorUrls);
            foreach (var (mirror, reason) in await FindInvalidMirrorsAsync(task, CancellationToken.None).ConfigureAwait(false))
            {
                Trace.TraceWarning($"Mirror rejected: {mirror}: {reason}");
                task.MirrorUrls.Remove(mirror);
            }
        }

        _tasks[task.Id] = task;
        await SaveStateAsync().ConfigureAwait(false);
        TaskAdded?.Invoke(task);

        if (autoStart)
        {
            ProcessDownloadQueue();
        }

        return task;
    }

    private void ProcessDownloadQueue()
    {
        if (_isDisposed) return;
        lock (_queueLock)
        {
            if (_isDisposed) return;
            while (true)
            {
                int activeCount = _tasks.Values.Count(t =>
                    t.Status == DownloadStatus.Downloading ||
                    t.Status == DownloadStatus.Assembling);

                if (MaxConcurrentDownloads > 0 && activeCount >= MaxConcurrentDownloads)
                {
                    break;
                }

                var now = DateTime.UtcNow;
                var nextTask = _tasks.Values
                    .Where(t => t.Status == DownloadStatus.Queued && !DownloadSchedule.IsWaiting(t, now))
                    .OrderBy(t => t.CreatedAt)
                    .FirstOrDefault();

                if (nextTask == null)
                {
                    break;
                }

                StartTaskInternal(nextTask.Id);
            }
        }
    }

    private void StartTaskInternal(Guid taskId)
    {
        lock (_queueLock)
        {
            if (!_tasks.TryGetValue(taskId, out var task))
            {
                return;
            }

            if (task.Status == DownloadStatus.Completed)
            {
                return;
            }

            if (task.Status == DownloadStatus.Downloading || task.Status == DownloadStatus.Assembling)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _runningTokens[taskId] = cts;
            task.Status = DownloadStatus.Downloading;
            task.ErrorMessage = null;
            task.LinkExpired = false;
            TaskUpdated?.Invoke(task);

            var runTask = Task.Run(() => ExecuteDownloadAsync(task, cts.Token));
            _runningExecutionTasks[taskId] = runTask;
        }
    }

    public void StartTask(Guid taskId)
    {
        lock (_queueLock)
        {
            StartTaskInternal(taskId);
            ProcessDownloadQueue();
        }
    }

    public async Task PauseTaskAsync(Guid taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            if (task.Status != DownloadStatus.Downloading &&
                task.Status != DownloadStatus.Assembling &&
                task.Status != DownloadStatus.Queued)
            {
                return;
            }

            Task? execTask = null;
            CancellationTokenSource? ctsToDispose = null;

            lock (_queueLock)
            {
                if (_runningTokens.TryRemove(taskId, out var cts))
                {
                    cts.Cancel();
                    _runningExecutionTasks.TryRemove(taskId, out execTask);
                    ctsToDispose = cts;
                }
            }

            if (execTask != null)
            {
                try { await execTask.ConfigureAwait(false); } catch { }
            }

            ctsToDispose?.Dispose();

            task.Status = DownloadStatus.Paused;
            task.SpeedBytesPerSec = 0;
            task.StartAt = null; // pausing a scheduled task cancels the schedule

            TaskUpdated?.Invoke(task);
            await SaveStateAsync().ConfigureAwait(false);
            ProcessDownloadQueue();
        }
    }

    public async Task ResumeTaskAsync(Guid taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            if (task.Status == DownloadStatus.Paused || task.Status == DownloadStatus.Failed || task.Status == DownloadStatus.Canceled)
            {
                task.Status = DownloadStatus.Queued;
                TaskUpdated?.Invoke(task);
                await SaveStateAsync().ConfigureAwait(false);
                ProcessDownloadQueue();
            }
        }
        await Task.CompletedTask;
    }

    public const string LinkExpiredMessage = "BaÄŸlantÄ±nÄ±n sÃ¼resi dolmuÅŸ olabilir. Sayfadan dosyayÄ± yeniden indir; Vortex kaldÄ±ÄŸÄ± yerden devam eder.";

    public const string AuthFailedMessage = "Kimlik doğrulama gerekli / hatalı. Kullanıcı adı ve şifreyi kontrol edip yeniden dene.";

    // A 401 from a site the user gave credentials for (or from a plain link with no page to refresh it from) is a login
    // problem: it must not mark the source dead or ask for a fresh link. Browser-captured links keep the expired-link path.
    private static bool IsAuthError(DownloadTask task, Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } &&
        (task.Credential != null || task.SourcePageUrl == null);

    private static bool IsLinkExpiredError(Exception ex) => ex is HttpRequestException
    {
        StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone
    };

    /// <summary>
    /// Points an unfinished task at a fresh link for the same file and resumes it, keeping downloaded chunks.
    /// Returns null on success, otherwise a user-facing reason (the task is left untouched).
    /// </summary>
    public async Task<string?> RefreshTaskUrlAsync(
        Guid taskId,
        string newUrl,
        string? cookies = null,
        string? referrer = null,
        string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return "Ä°ndirme bulunamadÄ±.";
        if (task.Status is DownloadStatus.Downloading or DownloadStatus.Assembling or DownloadStatus.Completed)
            return "Bu indirme ÅŸu an yenilenemez (aktif ya da tamamlanmÄ±ÅŸ).";

        newUrl = HttpCredential.StripUserInfo(newUrl, out _, out _);
        // The task's credential stays bound to the origin it was given for: a fresh link on another host never gets it.
        var meta = await UrlMetadataInspector.InspectAsync(
            _httpClient, newUrl, cancellationToken, cookies, referrer, userAgent, task.Credential).ConfigureAwait(false);
        if (DescribeMismatch(task, meta) is { } mismatch) return mismatch;

        task.Url = newUrl;
        task.Cookies = cookies;
        task.Referrer = referrer;
        task.UserAgent = userAgent ?? task.UserAgent;
        task.ETag ??= meta.ETag;
        task.LastModified ??= meta.LastModified;
        task.SupportsRange = true;
        task.LinkExpired = false;
        task.ErrorMessage = null;
        TaskUpdated?.Invoke(task);
        await SaveStateAsync().ConfigureAwait(false);
        await ResumeTaskAsync(taskId).ConfigureAwait(false);
        return null;
    }

    /// <summary>Null when <paramref name="meta"/> can serve the same bytes as the task (Range, same size, same ETag/Last-Modified), otherwise the reason.</summary>
    private static string? DescribeMismatch(DownloadTask task, UrlMetadata meta, bool checkValidators = true)
    {
        if (!meta.SupportsRange)
            return "Yeni baÄŸlantÄ± kaldÄ±ÄŸÄ± yerden devam etmeyi (Range) desteklemiyor ya da eriÅŸilemiyor.";
        if (task.TotalBytes <= 0 || meta.ContentLength != task.TotalBytes)
            return $"Dosya boyutu eÅŸleÅŸmiyor (beklenen {DownloadTask.FormatBytes(task.TotalBytes)}, yeni baÄŸlantÄ± {DownloadTask.FormatBytes(meta.ContentLength)}).";
        if (checkValidators && (
            (!string.IsNullOrEmpty(task.ETag) && !string.IsNullOrEmpty(meta.ETag) && task.ETag != meta.ETag) ||
            (!string.IsNullOrEmpty(task.LastModified) && !string.IsNullOrEmpty(meta.LastModified) && task.LastModified != meta.LastModified)))
            return "Sunucudaki dosya deÄŸiÅŸmiÅŸ (ETag/Last-Modified farklÄ±); kaldÄ±ÄŸÄ± yerden devam etmek bozuk dosya Ã¼retir.";
        return null;
    }

    public const int MaxMirrors = 8;

    /// <summary>Trimmed, de-duplicated http/https mirrors (primary excluded), at most <see cref="MaxMirrors"/>.</summary>
    public static List<string> NormalizeMirrorUrls(string primaryUrl, IEnumerable<string>? mirrorUrls)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { primaryUrl.Trim() };
        if (Uri.TryCreate(primaryUrl.Trim(), UriKind.Absolute, out var primary)) seen.Add(primary.AbsoluteUri);

        var result = new List<string>();
        foreach (string? raw in mirrorUrls ?? [])
        {
            if (result.Count >= MaxMirrors) break;
            if (Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                // user:pass@ on a mirror is dropped: credentials belong to the primary's origin only, and URLs get logged.
                HttpCredential.StripUserInfo(uri.AbsoluteUri, out _, out _) is var clean && seen.Add(clean))
            {
                result.Add(clean);
            }
        }
        return result;
    }

    /// <summary>Probes every mirror; returns those that cannot serve the same file, with the reason.</summary>
    private async Task<List<(string Url, string Reason)>> FindInvalidMirrorsAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        // The primary's cookies/referrer belong to its host and are never sent to a mirror.
        var metas = await Task.WhenAll(task.MirrorUrls.Select(m =>
            UrlMetadataInspector.InspectAsync(_httpClient, m, cancellationToken, userAgent: task.UserAgent,
                credential: task.Credential))).ConfigureAwait(false); // HeaderFor sends it only to the credential's origin
        return metas
            // Different servers emit different ETag/Last-Modified for the same file, so mirrors are matched by size only.
            .Select(meta => (meta.Url, Reason: DescribeMismatch(task, meta, checkValidators: false)))
            .Where(x => x.Reason != null)
            .Select(x => (x.Url, x.Reason!))
            .ToList();
    }

    // A full or locked disk is our problem, not the server's: failing over would wrongly kill healthy mirrors.
    public static bool IsLocalDiskError(Exception ex) =>
        ex is DirectoryNotFoundException or PathTooLongException ||
        (ex is IOException && (ex.HResult & 0xFFFF) is 0x70 or 0x27 or 0x20 or 0x21); // disk full, handle-disk-full, sharing, lock violation

    private static void MarkSourceDead(DownloadTask task, string source, string reason)
    {
        lock (task.ChunksLock)
        {
            if (task.DeadSources.Contains(source)) return;
            task.DeadSources = [.. task.DeadSources, source];
        }
        Trace.TraceWarning($"Download source disabled for this run: {source}: {reason}");
    }

    public async Task CancelTaskAsync(Guid taskId)
    {
        await PauseTaskAsync(taskId).ConfigureAwait(false);
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Status = DownloadStatus.Canceled;
            task.SpeedBytesPerSec = 0;
            TaskUpdated?.Invoke(task);
            await SaveStateAsync().ConfigureAwait(false);
            ProcessDownloadQueue();
        }
    }

    public async Task RemoveTaskAsync(Guid taskId)
    {
        await CancelTaskAsync(taskId).ConfigureAwait(false);
        if (_postCheckTokens.TryGetValue(taskId, out var checkCts))
        {
            try { checkCts.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (_tasks.TryRemove(taskId, out var task))
        {
            _lastDownloadedBytes.TryRemove(taskId, out _);
            CleanupTaskTempDirectorySafe(task.TempDirectory);
            TaskRemoved?.Invoke(task);
            await SaveStateAsync().ConfigureAwait(false);
        }
    }

    private static void CleanupTaskTempDirectorySafe(string? tempDir)
    {
        if (!string.IsNullOrWhiteSpace(tempDir) && Directory.Exists(tempDir))
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    public void StartAll()
    {
        lock (_queueLock)
        {
            foreach (var task in _tasks.Values)
            {
                if (task.Status == DownloadStatus.Paused || task.Status == DownloadStatus.Failed)
                {
                    task.Status = DownloadStatus.Queued;
                    TaskUpdated?.Invoke(task);
                }
            }
            ProcessDownloadQueue();
        }
    }

    public async Task PauseAllAsync()
    {
        var activeIds = _runningTokens.Keys.ToList();
        foreach (var id in activeIds)
        {
            await PauseTaskAsync(id).ConfigureAwait(false);
        }
    }

    public void SetGlobalSpeedLimit(long bytesPerSec)
    {
        _globalSpeedLimiter.SetLimit(bytesPerSec);
    }

    public static bool HasEnoughDiskSpace(string path, long requiredBytes, out long availableBytes)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                availableBytes = drive.AvailableFreeSpace;
                // Require file size + 50 MB safety margin
                return requiredBytes <= 0 || availableBytes >= (requiredBytes + 50L * 1024 * 1024);
            }
        }
        catch
        {
            // If unable to query drive info, do not block download
        }

        availableBytes = -1;
        return true;
    }

    private async Task ExecuteDownloadAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        SleepPreventer.StartPreventingSleep();
        try
        {
            task.DeadSources = [];
            Exception? primaryError = null;

            // Motrix Fix: Pre-flight disk space validation to prevent silent crash and freeze
            if (!HasEnoughDiskSpace(task.DestinationPath, task.TotalBytes, out long availableBytes))
            {
                long reqMb = task.TotalBytes / 1024 / 1024;
                long availMb = availableBytes / 1024 / 1024;
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = $"Yetersiz disk alanÄ±! Gerekli: {reqMb} MB, BoÅŸ alan: {availMb} MB.";
                TaskFailed?.Invoke(task, task.ErrorMessage);
                return;
            }

            // Resume Integrity Validation: verify remote resource has not been modified (ETag / Last-Modified / Length)
            if (task.DownloadedBytes > 0)
            {
                try
                {
                    var freshMeta = await UrlMetadataInspector.InspectAsync(
                        _httpClient, task.Url, cancellationToken,
                        task.Cookies, task.Referrer, task.UserAgent, task.Credential).ConfigureAwait(false);

                    // A different size usually means the signed link now serves an error page: keep the chunks, ask for a fresh link.
                    if (task.TotalBytes > 0 && freshMeta.ContentLength > 0 && task.TotalBytes != freshMeta.ContentLength)
                    {
                        throw new HttpRequestException("Content-Length changed", null, System.Net.HttpStatusCode.Gone);
                    }

                    bool resourceChanged = false;

                    if (!string.IsNullOrEmpty(task.ETag) && !string.IsNullOrEmpty(freshMeta.ETag) &&
                        !string.Equals(task.ETag, freshMeta.ETag, StringComparison.Ordinal))
                    {
                        resourceChanged = true;
                    }
                    else if (!string.IsNullOrEmpty(task.LastModified) && !string.IsNullOrEmpty(freshMeta.LastModified) &&
                             !string.Equals(task.LastModified, freshMeta.LastModified, StringComparison.Ordinal))
                    {
                        resourceChanged = true;
                    }

                    if (resourceChanged)
                    {
                        // Remote resource altered while paused/interrupted. Reset chunks to avoid corrupt data assembly.
                        task.ETag = freshMeta.ETag;
                        task.LastModified = freshMeta.LastModified;
                        task.TotalBytes = freshMeta.ContentLength;
                        task.SupportsRange = freshMeta.SupportsRange;
                        int initialChunks = freshMeta.SupportsRange ? task.MaxChunks : 1;

                        if (Directory.Exists(task.TempDirectory))
                        {
                            try
                            {
                                Directory.Delete(task.TempDirectory, true);
                                Directory.CreateDirectory(task.TempDirectory);
                            }
                            catch { }
                        }

                        lock (task.ChunksLock)
                        {
                            task.Chunks = ChunksManager.InitializeChunks(task.Id, task.TotalBytes, initialChunks, task.TempDirectory);
                            task.RecalculateDownloadedBytes();
                        }

                        await SaveStateAsync().ConfigureAwait(false);
                        TaskUpdated?.Invoke(task);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsLinkExpiredError(ex) && task.MirrorUrls.Count > 0)
                {
                    // Primary link is gone, but the mirrors may still serve the file.
                    primaryError = ex;
                    MarkSourceDead(task, task.Url, ex.Message);
                }
                catch (Exception ex) when (!IsLinkExpiredError(ex))
                {
                    // Fail-safe: transient network issues during pre-flight resume inspection do not abort ongoing retry
                }

                if (task.MirrorUrls.Count > 0)
                {
                    // A mirror that fails here is only skipped for this run (it may be a transient outage).
                    var invalid = await FindInvalidMirrorsAsync(task, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var (mirror, reason) in invalid) MarkSourceDead(task, mirror, reason);
                }
            }

            // If Range is not supported or single chunk
            if (!task.SupportsRange || (task.Chunks.Count <= 1 && task.MirrorUrls.Count == 0))
            {
                var singleChunk = task.Chunks[0];
                singleChunk.IsActive = true;
                try
                {
                    await ChunkDownloader.DownloadChunkAsync(
                        _httpClient,
                        task.Url,
                        singleChunk,
                        _globalSpeedLimiter,
                        onBytesReceived: null,
                        cancellationToken,
                        supportsRange: task.SupportsRange,
                        cookies: task.Cookies,
                        referrer: task.Referrer,
                        userAgent: task.UserAgent,
                        credential: task.Credential).ConfigureAwait(false);
                }
                finally
                {
                    singleChunk.IsActive = false;
                }
            }
            else
            {
                // Multi-chunk dynamic worker orchestration
                await RunDynamicChunkWorkersAsync(task, primaryError, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Strict verification: ensure all chunks are completed before assembly
            lock (task.ChunksLock)
            {
                if (task.Chunks.Any(c => !c.IsCompleted))
                {
                    throw new InvalidOperationException("BazÄ± indirme parÃ§alarÄ± tamamlanamadÄ±; birleÅŸtirme iptal edildi.");
                }
            }

            // All chunks downloaded -> Assemble file
            task.Status = DownloadStatus.Assembling;
            task.SpeedBytesPerSec = 0;
            TaskUpdated?.Invoke(task);

            var assemblyProgress = new Progress<double>(_ =>
            {
                TaskUpdated?.Invoke(task);
            });

            await FileAssembler.AssembleAsync(task, assemblyProgress, cancellationToken).ConfigureAwait(false);

            task.Status = DownloadStatus.Completed;
            task.CompletedAt = DateTime.UtcNow;
            task.SpeedBytesPerSec = 0;
            task.RecalculateDownloadedBytes();
            task.Sha256 = null;
            StartPostDownloadChecks(task);
            await SaveStateAsync().ConfigureAwait(false);
            TaskCompleted?.Invoke(task);
            TaskUpdated?.Invoke(task);
        }
        catch (OperationCanceledException)
        {
            // Handled as paused/canceled
            if (task.Status == DownloadStatus.Downloading || task.Status == DownloadStatus.Assembling)
            {
                task.Status = DownloadStatus.Paused;
            }
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (task.Status == DownloadStatus.Downloading || task.Status == DownloadStatus.Assembling)
                {
                    task.Status = DownloadStatus.Paused;
                }
                return;
            }

            task.Status = DownloadStatus.Failed;
            bool authError = IsAuthError(task, ex);
            task.LinkExpired = !authError && IsLinkExpiredError(ex);
            task.ErrorMessage = authError ? AuthFailedMessage : task.LinkExpired ? LinkExpiredMessage : ex.Message;
            task.SpeedBytesPerSec = 0;
            await SaveStateAsync().ConfigureAwait(false);
            TaskFailed?.Invoke(task, ex.Message);
            TaskUpdated?.Invoke(task);
        }
        finally
        {
            SleepPreventer.StopPreventingSleep();
            if (_runningTokens.TryRemove(task.Id, out var cts))
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
            _runningExecutionTasks.TryRemove(task.Id, out _);
            ProcessDownloadQueue();
        }
    }

    private void StartPostDownloadChecks(DownloadTask task)
    {
        if (_isDisposed) return;
        var cts = new CancellationTokenSource();
        if (!_postCheckTokens.TryAdd(task.Id, cts))
        {
            cts.Dispose();
            return;
        }
        task.ScanStatus = ScanStatus.Scanning;
        var run = Task.Run(() => RunPostDownloadChecksAsync(task, cts));
        _postCheckTasks[task.Id] = run;
        // Registered after the add, so a run that finishes instantly cannot leave a stale entry behind.
        _ = run.ContinueWith(t => _postCheckTasks.TryRemove(new KeyValuePair<Guid, Task>(task.Id, t)), TaskScheduler.Default);
    }

    private async Task RunPostDownloadChecksAsync(DownloadTask task, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await _postCheckGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!File.Exists(task.DestinationPath))
                {
                    task.ScanStatus = ScanStatus.None;
                }
                else
                {
                    try
                    {
                        task.Sha256 = await FileHasher.ComputeSha256Async(task.DestinationPath, ct).ConfigureAwait(false);
                        TaskUpdated?.Invoke(task);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // File moved/locked: no hash, still try to scan below.
                    }

                    task.ScanStatus = ScanWithDefender
                        ? await _scanner.ScanAsync(task.DestinationPath, ct).ConfigureAwait(false)
                        : ScanStatus.None;
                }
            }
            finally
            {
                _postCheckGate.Release();
            }

            if (task.ScanStatus == ScanStatus.Threat) ThreatDetected?.Invoke(task);
            TaskUpdated?.Invoke(task);
            await SaveStateAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Deleted or app closing: Scanning stays persisted so the check reruns on next start.
        }
        finally
        {
            _postCheckTokens.TryRemove(task.Id, out _);
            cts.Dispose();
        }
    }

    private async Task RunDynamicChunkWorkersAsync(DownloadTask task, Exception? primaryError, CancellationToken cancellationToken)
    {
        List<Task> activeWorkerTasks = [];
        object chunkListLock = task.ChunksLock;

        // Sources = primary + mirrors. Each connection goes to the live source with the fewest connections,
        // which is round-robin at start and stays balanced as chunks finish and split.
        string primaryUrl = task.Url;
        var sourceLoad = new[] { primaryUrl }.Concat(task.MirrorUrls).Distinct().ToDictionary(s => s, _ => 0);

        string TakeSource()
        {
            lock (chunkListLock)
            {
                var live = sourceLoad.Keys.Where(s => !task.DeadSources.Contains(s)).ToList();
                if (live.Count == 0)
                {
                    // Every source failed: report the primary's error (it decides LinkExpired).
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(
                        primaryError ?? new HttpRequestException("No live download source left."));
                }
                string source = live.MinBy(s => sourceLoad[s])!;
                sourceLoad[source]++;
                return source;
            }
        }

        async Task WorkerLoopAsync(DownloadChunk initialChunk)
        {
            DownloadChunk? currentChunk = initialChunk;

            while (currentChunk != null && !cancellationToken.IsCancellationRequested)
            {
                currentChunk.IsActive = true;
                bool failover = false;
                string? source = null;
                try
                {
                    source = TakeSource();
                    bool isPrimary = source == primaryUrl;
                    await ChunkDownloader.DownloadChunkAsync(
                        _httpClient,
                        source,
                        currentChunk,
                        _globalSpeedLimiter,
                        onBytesReceived: null,
                        cancellationToken,
                        supportsRange: task.SupportsRange,
                        // The primary's cookies/referrer belong to its host and are never sent to a mirror.
                        cookies: isPrimary ? task.Cookies : null,
                        referrer: isPrimary ? task.Referrer : null,
                        userAgent: task.UserAgent,
                        // Safe for every source: HeaderFor only answers for the origin the user entered it for.
                        credential: task.Credential).ConfigureAwait(false);
                }
                // shortcut: a 401 fails the task but sibling workers on healthy sources finish their in-flight chunks first;
                // cancel them via a linked token (and keep that cancel out of this failover filter) if the delay matters.
                catch (Exception ex) when (source != null && !cancellationToken.IsCancellationRequested && !IsLocalDiskError(ex) &&
                    !IsAuthError(task, ex) &&
                    ex is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
                {
                    // ChunkDownloader already retried: give up on this source and move the rest of the chunk to another one.
                    lock (chunkListLock)
                    {
                        // Record the primary's error in the same lock as marking it dead: otherwise another worker's
                        // TakeSource can see "all dead" first and throw a generic error, losing LinkExpired.
                        if (source == primaryUrl) primaryError ??= ex;
                        MarkSourceDead(task, source, ex.Message);
                        if (sourceLoad.Keys.All(task.DeadSources.Contains))
                        {
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(primaryError ?? ex);
                        }
                    }
                    failover = true;
                }
                finally
                {
                    if (source != null)
                    {
                        lock (chunkListLock) { sourceLoad[source]--; }
                    }
                    // Stays active during failover so no other worker picks the same chunk up.
                    if (!failover) currentChunk.IsActive = false;
                }

                if (failover) continue;

                currentChunk = null;

                // Dynamic Worker Attachment:
                // If this worker finished its chunk, check if another active chunk has enough un-downloaded bytes to split!
                lock (chunkListLock)
                {
                    // No Count cap: concurrency is bounded by the worker count; min split size is enforced by ChunksManager.
                    var maxFreeChunk = ChunksManager.FindMaxFreeChunk(task.Chunks);
                    if (maxFreeChunk != null)
                    {
                        int newId = task.Chunks.Count; // chunks are append-only, so ids stay unique
                        string newTempFile = Path.Combine(task.TempDirectory, $"{task.Id}_part_{newId}.bin");
                        var splitResult = ChunksManager.SplitChunk(maxFreeChunk, newId, newTempFile);
                        if (splitResult.HasValue)
                        {
                            var newChunk = splitResult.Value.NewChunk;
                            newChunk.IsActive = true;
                            task.Chunks.Add(newChunk);
                            currentChunk = newChunk;
                        }
                    }

                    // If still no chunk found, look for any incomplete unassigned chunk
                    if (currentChunk == null)
                    {
                        currentChunk = task.Chunks.FirstOrDefault(c => !c.IsCompleted && !c.IsActive);
                        if (currentChunk != null)
                        {
                            currentChunk.IsActive = true;
                        }
                    }
                }
            }
        }

        // Start workers for initial incomplete chunks
        List<DownloadChunk> toStart;
        lock (chunkListLock)
        {
            toStart = task.Chunks.Where(c => !c.IsCompleted && !c.IsActive).Take(task.MaxChunks).ToList();
            foreach (var chunk in toStart)
            {
                chunk.IsActive = true;
            }
        }

        foreach (var chunk in toStart)
        {
            activeWorkerTasks.Add(WorkerLoopAsync(chunk));
        }

        await Task.WhenAll(activeWorkerTasks).ConfigureAwait(false);
    }

    private void OnMonitorTick(object? state)
    {
        long currentTicks = Stopwatch.GetTimestamp();
        long diffTicks = currentTicks - _lastMonitorTicks;
        if (diffTicks <= 0) return;

        double elapsedSeconds = (double)diffTicks / Stopwatch.Frequency;
        _lastMonitorTicks = currentTicks;

        bool hasChanges = false;
        bool scheduleDue = false;
        var utcNow = DateTime.UtcNow;
        foreach (var task in _tasks.Values)
        {
            if (DownloadSchedule.IsDue(task, utcNow))
            {
                // Start time reached (or passed while the app was closed): hand it to the normal queue.
                task.StartAt = null;
                scheduleDue = true;
                TaskUpdated?.Invoke(task);
            }

            if (task.Status == DownloadStatus.Downloading)
            {
                task.ActiveSeconds += elapsedSeconds;
                task.RecalculateDownloadedBytes();
                long currentBytes = task.DownloadedBytes;
                _lastDownloadedBytes.TryGetValue(task.Id, out long previousBytes);
                _lastDownloadedBytes[task.Id] = currentBytes;

                long delta = Math.Max(0, currentBytes - previousBytes);
                task.SpeedBytesPerSec = delta / elapsedSeconds;
                hasChanges = true;
                TaskUpdated?.Invoke(task);
            }
            else
            {
                if (task.SpeedBytesPerSec > 0)
                {
                    task.SpeedBytesPerSec = 0;
                    TaskUpdated?.Invoke(task);
                }
            }
        }

        if (scheduleDue)
        {
            ProcessDownloadQueue();
            _ = SaveStateAsync();
        }

        if (hasChanges && Interlocked.Increment(ref _stateSaveTickCounter) % StateSaveIntervalTicks == 0)
        {
            // Decoupled periodic background save (every 5 seconds) prevents disk thrashing and lock contention
            _ = SaveStateAsync();
        }
    }

    public async Task SaveStateAsync()
    {
        try
        {
            var snapshot = Tasks.Select(t =>
            {
                lock (t.ChunksLock)
                {
                    return new DownloadTask
                    {
                        Id = t.Id,
                        Url = t.Url,
                        DestinationPath = t.DestinationPath,
                        FileName = t.FileName,
                        TotalBytes = t.TotalBytes,
                        DownloadedBytes = t.DownloadedBytes,
                        SpeedBytesPerSec = t.SpeedBytesPerSec,
                        Status = t.Status,
                        ETag = t.ETag,
                        LastModified = t.LastModified,
                        SupportsRange = t.SupportsRange,
                        MaxChunks = t.MaxChunks,
                        Chunks = new List<DownloadChunk>(t.Chunks),
                        ErrorMessage = t.ErrorMessage,
                        CreatedAt = t.CreatedAt,
                        CompletedAt = t.CompletedAt,
                        TempDirectory = t.TempDirectory,
                        // Persisted in plain text in the local state file; required to resume authenticated downloads after restart.
                        Cookies = t.Cookies,
                        Referrer = t.Referrer,
                        UserAgent = t.UserAgent,
                        Sha256 = t.Sha256,
                        ScanStatus = t.ScanStatus,
                        SourcePageUrl = t.SourcePageUrl,
                        LinkExpired = t.LinkExpired,
                        MirrorUrls = [.. t.MirrorUrls],
                        StartAt = t.StartAt,
                        ExpectedSha256 = t.ExpectedSha256,
                        ActiveSeconds = t.ActiveSeconds,
                        AuthUser = t.AuthUser,
                        AuthOrigin = t.AuthOrigin,
                        AuthPassword = t.AuthPassword // written DPAPI-protected (AuthPasswordProtected)
                    };
                }
            }).ToList();

            await _stateStore.SaveTasksAsync(snapshot).ConfigureAwait(false);
        }
        catch
        {
            // Background save fail-safe
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await _monitorTimer.DisposeAsync().ConfigureAwait(false);

        // 1. Signal cancellation to all workers
        foreach (var cts in _runningTokens.Values)
        {
            try { cts.Cancel(); } catch { }
        }

        // 2. Await background execution tasks so they finish cleanly
        var runningTasks = _runningExecutionTasks.Values.ToArray();
        if (runningTasks.Length > 0)
        {
            try { await Task.WhenAll(runningTasks).ConfigureAwait(false); } catch { }
        }

        // 3. Dispose CTS instances safely
        foreach (var cts in _runningTokens.Values)
        {
            try { cts.Dispose(); } catch { }
        }
        _runningTokens.Clear();
        _runningExecutionTasks.Clear();

        // 4. Stop post-download checks (each run disposes its own CTS)
        foreach (var cts in _postCheckTokens.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        try { await Task.WhenAll(_postCheckTasks.Values.ToArray()).ConfigureAwait(false); } catch { }

        await SaveStateAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }
}
