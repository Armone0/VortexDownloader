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

    public DownloadEngine(StateStore? stateStore = null, HttpClient? httpClient = null, long globalSpeedLimit = 0)
    {
        _stateStore = stateStore ?? new StateStore();
        _httpClient = httpClient ?? ChunkDownloader.CreateOptimizedHttpClient();
        _globalSpeedLimiter = new SpeedLimiter(globalSpeedLimit);

        _lastMonitorTicks = Stopwatch.GetTimestamp();
        _monitorTimer = new Timer(OnMonitorTick, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    public async Task InitializeAsync()
    {
        var savedTasks = await _stateStore.LoadTasksAsync().ConfigureAwait(false);
        foreach (var task in savedTasks)
        {
            _tasks[task.Id] = task;
            TaskAdded?.Invoke(task);
        }
    }

    public async Task<UrlMetadata> InspectUrlAsync(
        string url,
        CancellationToken cancellationToken = default,
        string? cookies = null,
        string? referrer = null,
        string? userAgent = null)
    {
        return await UrlMetadataInspector.InspectAsync(
            _httpClient, url, cancellationToken, cookies, referrer, userAgent).ConfigureAwait(false);
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
        string? sourcePageUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        // Fetch URL metadata only if not preloaded (preserves single-use tokens!)
        var metadata = preloadedMetadata ?? await InspectUrlAsync(url, CancellationToken.None, cookies, referrer, userAgent).ConfigureAwait(false);

        string fileName = Path.GetFileName(destinationPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = metadata.SuggestedFileName;
            destinationPath = Path.Combine(destinationPath, fileName);
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
            SourcePageUrl = sourcePageUrl ?? referrer
        };

        // Initialize initial chunks
        int initialChunks = metadata.SupportsRange ? task.MaxChunks : 1;
        task.Chunks = ChunksManager.InitializeChunks(task.Id, task.TotalBytes, initialChunks, tempDir);

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

                var nextTask = _tasks.Values
                    .Where(t => t.Status == DownloadStatus.Queued)
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

    public const string LinkExpiredMessage = "Bağlantının süresi dolmuş olabilir. Sayfadan dosyayı yeniden indir; Vortex kaldığı yerden devam eder.";

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
        if (!_tasks.TryGetValue(taskId, out var task)) return "İndirme bulunamadı.";
        if (task.Status is DownloadStatus.Downloading or DownloadStatus.Assembling or DownloadStatus.Completed)
            return "Bu indirme şu an yenilenemez (aktif ya da tamamlanmış).";

        var meta = await InspectUrlAsync(newUrl, cancellationToken, cookies, referrer, userAgent).ConfigureAwait(false);
        if (!meta.SupportsRange)
            return "Yeni bağlantı kaldığı yerden devam etmeyi (Range) desteklemiyor ya da erişilemiyor.";
        if (task.TotalBytes <= 0 || meta.ContentLength != task.TotalBytes)
            return $"Dosya boyutu eşleşmiyor (beklenen {DownloadTask.FormatBytes(task.TotalBytes)}, yeni bağlantı {DownloadTask.FormatBytes(meta.ContentLength)}).";
        if ((!string.IsNullOrEmpty(task.ETag) && !string.IsNullOrEmpty(meta.ETag) && task.ETag != meta.ETag) ||
            (!string.IsNullOrEmpty(task.LastModified) && !string.IsNullOrEmpty(meta.LastModified) && task.LastModified != meta.LastModified))
            return "Sunucudaki dosya değişmiş (ETag/Last-Modified farklı); kaldığı yerden devam etmek bozuk dosya üretir.";

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
            // Motrix Fix: Pre-flight disk space validation to prevent silent crash and freeze
            if (!HasEnoughDiskSpace(task.DestinationPath, task.TotalBytes, out long availableBytes))
            {
                long reqMb = task.TotalBytes / 1024 / 1024;
                long availMb = availableBytes / 1024 / 1024;
                task.Status = DownloadStatus.Failed;
                task.ErrorMessage = $"Yetersiz disk alanı! Gerekli: {reqMb} MB, Boş alan: {availMb} MB.";
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
                        task.Cookies, task.Referrer, task.UserAgent).ConfigureAwait(false);

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
                catch (Exception ex) when (!IsLinkExpiredError(ex))
                {
                    // Fail-safe: transient network issues during pre-flight resume inspection do not abort ongoing retry
                }
            }

            // If Range is not supported or single chunk
            if (!task.SupportsRange || task.Chunks.Count <= 1)
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
                        userAgent: task.UserAgent).ConfigureAwait(false);
                }
                finally
                {
                    singleChunk.IsActive = false;
                }
            }
            else
            {
                // Multi-chunk dynamic worker orchestration
                await RunDynamicChunkWorkersAsync(task, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Strict verification: ensure all chunks are completed before assembly
            lock (task.ChunksLock)
            {
                if (task.Chunks.Any(c => !c.IsCompleted))
                {
                    throw new InvalidOperationException("Bazı indirme parçaları tamamlanamadı; birleştirme iptal edildi.");
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
            task.LinkExpired = IsLinkExpiredError(ex);
            task.ErrorMessage = task.LinkExpired ? LinkExpiredMessage : ex.Message;
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

    private async Task RunDynamicChunkWorkersAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        List<Task> activeWorkerTasks = [];
        object chunkListLock = task.ChunksLock;

        async Task WorkerLoopAsync(DownloadChunk initialChunk)
        {
            DownloadChunk? currentChunk = initialChunk;

            while (currentChunk != null && !cancellationToken.IsCancellationRequested)
            {
                currentChunk.IsActive = true;
                try
                {
                    await ChunkDownloader.DownloadChunkAsync(
                        _httpClient,
                        task.Url,
                        currentChunk,
                        _globalSpeedLimiter,
                        onBytesReceived: null,
                        cancellationToken,
                        supportsRange: task.SupportsRange,
                        cookies: task.Cookies,
                        referrer: task.Referrer,
                        userAgent: task.UserAgent).ConfigureAwait(false);
                }
                finally
                {
                    currentChunk.IsActive = false;
                }

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
        foreach (var task in _tasks.Values)
        {
            if (task.Status == DownloadStatus.Downloading)
            {
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
                        SourcePageUrl = t.SourcePageUrl,
                        LinkExpired = t.LinkExpired
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

        await SaveStateAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }
}
