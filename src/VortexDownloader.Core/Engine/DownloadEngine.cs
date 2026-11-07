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
    private bool _isDisposed;

    public event Action<DownloadTask>? TaskAdded;
    public event Action<DownloadTask>? TaskUpdated;
    public event Action<DownloadTask>? TaskCompleted;
    public event Action<DownloadTask, string>? TaskFailed;
    public event Action<DownloadTask>? TaskRemoved;

    public SpeedLimiter GlobalSpeedLimiter => _globalSpeedLimiter;
    public IReadOnlyCollection<DownloadTask> Tasks => _tasks.Values.ToList();

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

    public async Task<UrlMetadata> InspectUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        return await UrlMetadataInspector.InspectAsync(_httpClient, url, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DownloadTask> AddDownloadAsync(
        string url,
        string destinationPath,
        int chunkCount = 8,
        bool autoStart = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        // Fetch URL metadata
        var metadata = await InspectUrlAsync(url).ConfigureAwait(false);

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
            MaxChunks = Math.Clamp(chunkCount, 1, 32),
            TempDirectory = tempDir,
            Status = DownloadStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };

        // Initialize initial chunks
        int initialChunks = metadata.SupportsRange ? task.MaxChunks : 1;
        task.Chunks = ChunksManager.InitializeChunks(task.Id, task.TotalBytes, initialChunks, tempDir);

        _tasks[task.Id] = task;
        await SaveStateAsync().ConfigureAwait(false);
        TaskAdded?.Invoke(task);

        if (autoStart)
        {
            StartTask(task.Id);
        }

        return task;
    }

    public void StartTask(Guid taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
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
        TaskUpdated?.Invoke(task);

        var runTask = Task.Run(() => ExecuteDownloadAsync(task, cts.Token));
        _runningExecutionTasks[taskId] = runTask;
    }

    public async Task PauseTaskAsync(Guid taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            if (_runningTokens.TryRemove(taskId, out var cts))
            {
                cts.Cancel();
                if (_runningExecutionTasks.TryRemove(taskId, out var execTask))
                {
                    try { await execTask.ConfigureAwait(false); } catch { }
                }
                cts.Dispose();
            }

            task.Status = DownloadStatus.Paused;
            task.SpeedBytesPerSec = 0;
            TaskUpdated?.Invoke(task);
            await SaveStateAsync().ConfigureAwait(false);
        }
    }

    public async Task ResumeTaskAsync(Guid taskId)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            if (task.Status == DownloadStatus.Paused || task.Status == DownloadStatus.Failed)
            {
                StartTask(taskId);
            }
        }
        await Task.CompletedTask;
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
        }
    }

    public async Task RemoveTaskAsync(Guid taskId)
    {
        await CancelTaskAsync(taskId).ConfigureAwait(false);
        if (_tasks.TryRemove(taskId, out var task))
        {
            _lastDownloadedBytes.TryRemove(taskId, out _);
            TaskRemoved?.Invoke(task);
            await SaveStateAsync().ConfigureAwait(false);
        }
    }

    public void StartAll()
    {
        foreach (var task in _tasks.Values)
        {
            if (task.Status == DownloadStatus.Queued || task.Status == DownloadStatus.Paused)
            {
                StartTask(task.Id);
            }
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

    private async Task ExecuteDownloadAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        try
        {
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
                        _ => task.RecalculateDownloadedBytes(),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    singleChunk.IsActive = false;
                }
            }
            else
            {
                // Multi-chunk dynamic IDM worker orchestration
                await RunDynamicChunkWorkersAsync(task, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

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
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.ErrorMessage = ex.Message;
            task.SpeedBytesPerSec = 0;
            await SaveStateAsync().ConfigureAwait(false);
            TaskFailed?.Invoke(task, ex.Message);
            TaskUpdated?.Invoke(task);
        }
        finally
        {
            _runningTokens.TryRemove(task.Id, out var cts);
            cts?.Dispose();
            _runningExecutionTasks.TryRemove(task.Id, out _);
        }
    }

    private async Task RunDynamicChunkWorkersAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        using var activeWorkersSemaphore = new SemaphoreSlim(task.MaxChunks, task.MaxChunks);
        List<Task> activeWorkerTasks = [];
        object chunkListLock = task.Chunks;

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
                        _ => task.RecalculateDownloadedBytes(),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    currentChunk.IsActive = false;
                }

                currentChunk = null;

                // IDM Dynamic Worker Attachment:
                // If this worker finished its chunk, check if another active chunk has enough un-downloaded bytes to split!
                lock (chunkListLock)
                {
                    if (task.Chunks.Count < task.MaxChunks)
                    {
                        var maxFreeChunk = ChunksManager.FindMaxFreeChunk(task.Chunks);
                        if (maxFreeChunk != null)
                        {
                            int newId = task.Chunks.Count;
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

        if (hasChanges)
        {
            // Non-blocking background save
            _ = SaveStateAsync();
        }
    }

    public async Task SaveStateAsync()
    {
        try
        {
            await _stateStore.SaveTasksAsync(Tasks.ToList()).ConfigureAwait(false);
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

        foreach (var cts in _runningTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _runningTokens.Clear();

        await SaveStateAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }
}
