using System.IO;
using System.Text.Json;
using VortexDownloader.Core.Models;

namespace VortexDownloader.Core.Storage;

public class StateStore
{
    private readonly string _storageFilePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public StateStore(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            _storageFilePath = customFilePath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _storageFilePath = Path.Combine(appData, "VortexDownloader", "tasks.json");
        }

        string? dir = Path.GetDirectoryName(_storageFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string StorageFilePath => _storageFilePath;

    public async Task SaveTasksAsync(IReadOnlyList<DownloadTask> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string tempFile = _storageFilePath + ".tmp";
            string? dir = Path.GetDirectoryName(_storageFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Serialize tasks snapshot
            await using (var stream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, tasks, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Atomic move / replace
            File.Move(tempFile, _storageFilePath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<List<DownloadTask>> LoadTasksAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_storageFilePath))
            {
                return [];
            }

            await using var stream = new FileStream(_storageFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var tasks = await JsonSerializer.DeserializeAsync<List<DownloadTask>>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            
            if (tasks == null)
            {
                return [];
            }

            // Restore in-memory derived states
            foreach (var task in tasks)
            {
                task.RecalculateDownloadedBytes();
                if (task.Status == DownloadStatus.Downloading)
                {
                    // If power cut occurred during download, mark as Paused so user can resume
                    task.Status = DownloadStatus.Paused;
                }
            }

            return tasks;
        }
        catch
        {
            // If file is corrupted or unreadable, return empty list gracefully
            return [];
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
